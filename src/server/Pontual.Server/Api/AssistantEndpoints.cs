using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Assistente de IA das atendentes. O terminal conversa por <c>/device/assistant</c>; a Central cadastra a
/// chave, os roteiros operacionais e o perfil de cada atendente, e lê as conversas.
/// A chave da IA nunca sai do servidor: o terminal só fala com o servidor da Pontual.
/// </summary>
public static class AssistantEndpoints
{
    public const string RateLimit = "assistant";
    public const string DefaultModel = "claude-opus-5-5";

    private static readonly (string Id, string Label)[] Models =
    [
        ("claude-opus-5-5", "Claude Opus 5.5 (mais capaz)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 (equilibrado)"),
        ("claude-haiku-5-5", "Claude Haiku 5.5 (mais rápido e barato)"),
    ];

    private const int MaxQuestion = 4000;
    private const int MaxScript = 60_000;
    private const int MaxAllScripts = KnowledgeLimits.All;
    private const int MaxProfileInstructions = 4000;
    private const int MaxGeneralInstructions = 8000;
    private const int MaxScripts = KnowledgeLimits.Items;
    private const int MaxFeedback = 2000;

    /// <summary>Quantas falas da conversa atual voltam para a IA como contexto.</summary>
    private const int ContextTurns = 40;
    private const int HistoryShown = 60;
    private const int ConversationsShown = 300;

    /// <summary>Depois deste tempo parada, a próxima pergunta abre uma conversa nova (um novo expediente).</summary>
    private static readonly TimeSpan ConversationIdle = TimeSpan.FromHours(8);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Máquinas com uma resposta em andamento: uma pergunta por vez por atendente.</summary>
    private static readonly ConcurrentDictionary<Guid, byte> Busy = new();

    private sealed record AskBody(string? Text, bool? NewConversation, ScreenBody? Screen, List<AppBody?>? Apps = null);
    /// <summary>Um site ou programa liberado no Terminal desta máquina, que a assistente pode abrir a pedido da atendente.</summary>
    private sealed record AppBody(string? Name, string? Kind);
    /// <summary>O que o Terminal leu da tela na hora da pergunta: partes em texto e a foto (JPEG em base64) da aba ativa.</summary>
    private sealed record ScreenBody(List<ScreenPartBody?>? Parts, string? Image);
    private sealed record ScreenPartBody(string? Kind, bool? Active, string? Name, string? Address, string? Title, string? Contact, string? Text);
    private sealed record SettingsBody(string? ApiKey, bool? RemoveKey, string? Model, string? Instructions, bool? ScreenContext);

    private const int MaxAskBody = 3 * 1024 * 1024;
    private const int MaxScreenParts = 4;
    private const int MaxScreenPart = 14_000;
    private const int MaxScreenImage = 900_000;
    private sealed record ScriptBody(string? Title, string? Content, bool? Enabled);
    private sealed record ProfileBody(string? Instructions, bool? Enabled);
    private sealed record FeedbackBody(long? MessageId, bool? Helpful, string? Text);

    public static void Map(RouteGroupBuilder api)
    {
        // Terminal.
        api.MapGet("/device/assistant", StateAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/assistant/messages", AskAsync).Terminal().RequireRateLimiting(RateLimit);
        api.MapPost("/device/assistant/feedback", FeedbackAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/assistant/whatsapp", WhatsAppAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);

        // Central.
        api.MapGet("/assistant", OverviewAsync).Panel(Permission.ConfigRead);
        api.MapPut("/assistant/settings", SaveSettingsAsync).Panel(Permission.ConfigEdit);
        api.MapPost("/assistant/test", TestAsync).Panel(Permission.ConfigEdit);
        api.MapGet("/assistant/scripts/{id:guid}", ScriptAsync).Panel(Permission.ConfigRead);
        api.MapPost("/assistant/scripts", CreateScriptAsync).Panel(Permission.ConfigEdit);
        api.MapPut("/assistant/scripts/{id:guid}", UpdateScriptAsync).Panel(Permission.ConfigEdit);
        api.MapDelete("/assistant/scripts/{id:guid}", DeleteScriptAsync).Panel(Permission.ConfigEdit);
        api.MapPut("/assistant/profiles/{deviceId:guid}", SaveProfileAsync).Panel(Permission.ConfigEdit);
        api.MapGet("/assistant/conversations/{deviceId:guid}", ConversationsAsync).Panel(Permission.UsageReadIndividual);
    }

    /// <summary>A assistente aparece para a máquina? Precisa de máquina aprovada, chave cadastrada e perfil ligado.</summary>
    public static async Task<bool> IsEnabledAsync(PontualDb db, IConfiguration configuration, Device device, CancellationToken ct)
    {
        if (!device.Approved)
        {
            return false;
        }

        var profileOff = await db.AssistantProfiles.AsNoTracking().AnyAsync(p => p.DeviceId == device.Id && !p.Enabled, ct);
        return !profileOff && await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is not null;
    }

    // ------------------------------------------------------------------ terminal

    private static async Task<IResult> StateAsync(HttpContext context, PontualDb db, IConfiguration configuration, CancellationToken ct)
    {
        var device = context.Device();
        var enabled = await IsEnabledAsync(db, configuration, device, ct);
        if (!enabled)
        {
            return Results.Ok(new { enabled, attendantName = device.AttendantName, messages = Array.Empty<object>() });
        }

        var screen = await ScreenAllowedAsync(db, ct);
        var whatsappLearning = await WhatsAppLearningAsync(db, configuration, device, ct);

        var messages = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.DeviceId == device.Id)
            .OrderByDescending(m => m.Id)
            .Take(HistoryShown)
            .ToListAsync(ct);
        messages.Reverse();

        var oldest = messages.Count > 0 ? messages[0].Id : 0;
        var feedback = await db.AssistantFeedback.AsNoTracking()
            .Where(f => f.DeviceId == device.Id && f.MessageId >= oldest)
            .Select(f => new { messageId = f.MessageId, helpful = f.Helpful })
            .ToListAsync(ct);

        return Results.Ok(new { enabled, attendantName = device.AttendantName, messages = messages.Select(MessageDto), feedback, screen, whatsappLearning });
    }

    /// <summary>A Central permite que a assistente receba a tela e o WhatsApp da atendente? (Ligado, se nunca foi mexido.)</summary>
    private static async Task<bool> ScreenAllowedAsync(PontualDb db, CancellationToken ct) =>
        !await db.TextSettings.AsNoTracking().AnyAsync(s => s.Key == TextSetting.AssistantScreen && s.Value == "0", ct);

    private sealed record WhatsAppBody(string? Contact, string? Text);

    private const int MaxTranscript = 40_000;

    /// <summary>O Terminal desta máquina deve registrar as conversas do WhatsApp para a análise?</summary>
    public static async Task<bool> WhatsAppLearningAsync(PontualDb db, IConfiguration configuration, Device device, CancellationToken ct) =>
        !await db.TextSettings.AsNoTracking().AnyAsync(s => s.Key == TextSetting.AssistantWhatsAppLearning && s.Value == "0", ct)
        && await IsEnabledAsync(db, configuration, device, ct);

    /// <summary>
    /// O Terminal manda, de tempos em tempos, as últimas mensagens da conversa de WhatsApp que está aberta.
    /// Cada contato tem uma linha na fila; o que chega é emendado ao que já estava. A fila só existe até a
    /// análise: depois de lida pela IA, a conversa é apagada.
    /// </summary>
    private static async Task<IResult> WhatsAppAsync(HttpContext context, PontualDb db, IConfiguration configuration, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var body = await context.ReadBodyAsync<WhatsAppBody>();
        var contact = Input.Truncated(body.Contact, 80);
        var text = Multiline(body.Text);
        // Desligado na Central (ou conversa vazia): o envio é aceito e descartado, sem erro para o Terminal.
        if (contact.Length == 0 || text.Length == 0 || !await WhatsAppLearningAsync(db, configuration, device, ct))
        {
            return Results.NoContent();
        }

        if (text.Length > MaxScreenPart)
        {
            text = text[^MaxScreenPart..];
        }

        var now = time.GetUtcNow().UtcDateTime;
        var capture = await db.WhatsAppCaptures.FirstOrDefaultAsync(c => c.DeviceId == device.Id && c.Contact == contact, ct);
        if (capture is null)
        {
            capture = new WhatsAppCapture { DeviceId = device.Id, Contact = contact, CreatedAt = now };
            db.WhatsAppCaptures.Add(capture);
        }

        var merged = MergeTranscript(capture.Text, text);
        if (merged != capture.Text)
        {
            capture.Text = merged.Length > MaxTranscript ? merged[^MaxTranscript..] : merged;
            capture.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Emenda o trecho novo ao que já estava guardado, sem repetir as mensagens que aparecem nos dois:
    /// procura o maior final do guardado que seja igual ao começo do novo.
    /// </summary>
    internal static string MergeTranscript(string stored, string snapshot)
    {
        if (stored.Length == 0)
        {
            return snapshot;
        }

        // A atendente rolou a conversa para cima: o trecho já está inteiro no que foi guardado.
        if (stored.Contains(snapshot, StringComparison.Ordinal))
        {
            return stored;
        }

        var old = stored.Split('\n');
        var fresh = snapshot.Split('\n');
        for (var overlap = Math.Min(old.Length, fresh.Length); overlap > 0; overlap--)
        {
            if (old.AsSpan(old.Length - overlap).SequenceEqual(fresh.AsSpan(0, overlap)))
            {
                return overlap == fresh.Length ? stored : stored + "\n" + string.Join('\n', fresh[overlap..]);
            }
        }

        return stored + "\n" + snapshot;
    }

    /// <summary>
    /// Confere e limita o que o Terminal mandou da tela. Devolve o texto que acompanha a pergunta, a foto e a
    /// nota curta que fica no histórico (só os nomes do que estava aberto; nenhum conteúdo da tela é gravado).
    /// </summary>
    private const int MaxApps = 60;

    /// <summary>
    /// A lista do que a assistente pode abrir neste Terminal. Só os nomes: quem abre é o próprio Terminal,
    /// que confere o nome na configuração dele (a IA não escolhe endereço).
    /// </summary>
    private static string? AppsText(List<AppBody?>? apps)
    {
        var names = (apps ?? []).OfType<AppBody>()
            .Select(a => (Name: Input.Truncated(a.Name, 60), Program: a.Kind == "program"))
            .Where(a => a.Name.Length > 0)
            .DistinctBy(a => a.Name)
            .Take(MaxApps)
            .Select(a => a.Program ? a.Name + " (programa)" : a.Name)
            .ToList();
        return names.Count == 0 ? null
            : "<terminal_pode_abrir>\n" + string.Join("\n", names) + "\n</terminal_pode_abrir>";
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + "\n\n" + b;

    private static (string? Text, byte[]? Image, string Note) ReadScreen(ScreenBody? screen)
    {
        if (screen is null)
        {
            return (null, null, "");
        }

        var parts = new List<AssistantPrompt.ScreenPart>();
        foreach (var raw in (screen.Parts ?? []).Take(MaxScreenParts))
        {
            if (raw is null)
            {
                continue;
            }

            var kind = raw.Kind is "whatsapp" or "home" ? raw.Kind : "page";
            var text = Multiline(raw.Text);
            if (text.Length > MaxScreenPart)
            {
                // Do WhatsApp interessa o fim (as mensagens mais recentes); de um site, o começo da página.
                text = kind == "whatsapp" ? text[^MaxScreenPart..] : text[..MaxScreenPart];
            }

            parts.Add(new AssistantPrompt.ScreenPart(
                kind, raw.Active == true, Input.Truncated(raw.Name, 60), Input.Truncated(raw.Address, 200),
                Input.Truncated(raw.Title, 200), Input.Truncated(raw.Contact, 80), text));
        }

        byte[]? image = null;
        if (screen.Image is { Length: > 0 and <= MaxScreenImage * 4 / 3 + 8 } encoded)
        {
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                // Só JPEG, que é o que o Terminal manda; qualquer outra coisa é descartada.
                if (bytes.Length is > 3 and <= MaxScreenImage && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                {
                    image = bytes;
                }
            }
            catch (FormatException)
            {
                // Foto ilegível: a pergunta segue só com o texto.
            }
        }

        var names = parts.Where(p => p.Kind != "home").Select(p => p.Kind == "whatsapp" ? "WhatsApp" : p.Name).Where(n => n.Length > 0).Distinct().ToList();
        var note = names.Count > 0 ? string.Join(", ", names) : parts.Count > 0 ? "Tela inicial" : image is not null ? "Tela" : "";
        return (AssistantPrompt.Screen(parts, image is not null), image, Input.Truncated(note, 120));
    }

    /// <summary>
    /// A atendente avalia uma resposta. "Não ajudou" com um texto vira uma sugestão de correção, que fica
    /// pendente na Central Neural: a assistente só passa a usar o que um administrador aprovar.
    /// </summary>
    private static async Task<IResult> FeedbackAsync(HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var body = await context.ReadBodyAsync<FeedbackBody>();
        if (body.MessageId is not { } messageId || body.Helpful is not { } helpful)
        {
            throw ApiError.BadRequest("Informe a resposta avaliada e se ela ajudou.");
        }

        var text = Multiline(body.Text);
        if (text.Length > MaxFeedback)
        {
            throw ApiError.BadRequest($"O comentário aceita no máximo {MaxFeedback} caracteres.");
        }

        var answer = await db.AssistantMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.DeviceId == device.Id && m.Role == AssistantRole.Assistant, ct)
            ?? throw ApiError.NotFound("Resposta não encontrada.");

        var now = time.GetUtcNow().UtcDateTime;
        var feedback = await db.AssistantFeedback.FirstOrDefaultAsync(f => f.DeviceId == device.Id && f.MessageId == messageId, ct);
        if (feedback is null)
        {
            feedback = new AssistantFeedback { DeviceId = device.Id, MessageId = messageId };
            db.AssistantFeedback.Add(feedback);
        }

        feedback.Helpful = helpful;
        feedback.Text = helpful ? "" : text;
        feedback.At = now;

        if (!helpful && text.Length > 0)
        {
            var question = await db.AssistantMessages.AsNoTracking()
                .Where(m => m.DeviceId == device.Id && m.ConversationId == answer.ConversationId && m.Id < messageId && m.Role == AssistantRole.User)
                .OrderByDescending(m => m.Id)
                .Select(m => m.Text)
                .FirstOrDefaultAsync(ct) ?? "";

            var suggestion = await db.KnowledgeSuggestions
                .FirstOrDefaultAsync(s => s.MessageId == messageId && s.DeviceId == device.Id && s.Status == SuggestionStatus.Pending, ct);
            if (suggestion is null)
            {
                suggestion = new KnowledgeSuggestion
                {
                    Id = Guid.NewGuid(),
                    Kind = SuggestionKind.Correction,
                    Title = Input.Truncated($"Correção de {device.Label}: {question.ReplaceLineEndings(" ")}", 120),
                    Source = "attendant",
                    DeviceId = device.Id,
                    MessageId = messageId,
                    AttendantName = device.Label,
                    Question = question.Length > 2000 ? question[..2000] : question,
                    Answer = answer.Text.Length > 6000 ? answer.Text[..6000] : answer.Text,
                    CreatedAt = now,
                };
                db.KnowledgeSuggestions.Add(suggestion);
            }

            suggestion.Detail = text;
            suggestion.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Recebe a pergunta e devolve a resposta em fluxo (text/event-stream), um JSON por linha "data:":
    /// <c>{"delta":"…"}</c> a cada pedaço, e no fim <c>{"done":true,"id":…,"at":…}</c> ou <c>{"error":"…"}</c>.
    /// </summary>
    private static async Task<IResult> AskAsync(HttpContext context, PontualDb db, IConfiguration configuration, IAssistantModel model, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        // A pergunta pode vir com a foto da tela: esta rota aceita um corpo maior que as demais.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxAskBody;
        }

        var body = await context.ReadBodyAsync<AskBody>();
        var question = Multiline(body.Text);
        if (question.Length == 0)
        {
            throw ApiError.BadRequest("Escreva a sua pergunta.");
        }

        if (question.Length > MaxQuestion)
        {
            throw ApiError.BadRequest($"A pergunta aceita no máximo {MaxQuestion} caracteres. Divida em partes menores.");
        }

        var apiKey = await IsEnabledAsync(db, configuration, device, ct) ? await AssistantSecrets.ReadKeyAsync(db, configuration, ct) : null;
        if (apiKey is null)
        {
            throw ApiError.Conflict("A assistente não está ligada para este computador.");
        }

        if (!Busy.TryAdd(device.Id, 0))
        {
            throw ApiError.Conflict("Aguarde a resposta anterior terminar.");
        }

        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            var last = await db.AssistantMessages.AsNoTracking()
                .Where(m => m.DeviceId == device.Id)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct);
            var conversationId = body.NewConversation == true || last is null || now - last.At > ConversationIdle
                ? Guid.NewGuid()
                : last.ConversationId;

            // A tela só entra se a Central permitir; e só nesta pergunta (as anteriores voltam à IA sem ela).
            var screen = await ScreenAllowedAsync(db, ct) ? ReadScreen(body.Screen) : (Text: null, Image: null, Note: "");

            var asked = new AssistantMessage { DeviceId = device.Id, ConversationId = conversationId, Role = AssistantRole.User, Text = question, At = now, Context = screen.Note };
            db.AssistantMessages.Add(asked);
            await db.SaveChangesAsync(ct);

            // No modo índice a IA consulta a base antes de responder; a atendente vê o que está sendo consultado.
            var request = await BuildRequestAsync(db, device, apiKey, conversationId, now, status => SendAsync(context, new { status }, ct), ct)
                with { ScreenText = Join(screen.Text, AppsText(body.Apps)), ScreenImage = screen.Image };

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await SendAsync(context, new { conversationId, questionId = asked.Id, screen = screen.Note }, ct);

            var answer = new StringBuilder();
            string? error = null;
            try
            {
                var stop = await model.AskAsync(request, async piece =>
                {
                    answer.Append(piece);
                    await SendAsync(context, new { delta = piece }, ct);
                }, ct);

                if (stop == AssistantStop.Refusal && answer.Length == 0)
                {
                    error = "A assistente não pôde responder a essa pergunta. Reformule ou procure a supervisão.";
                }
                else if (stop == AssistantStop.MaxTokens)
                {
                    const string cut = "\n\n(A resposta ficou longa demais e foi cortada. Peça para continuar.)";
                    answer.Append(cut);
                    await SendAsync(context, new { delta = cut }, ct);
                }
                else if (answer.Length == 0)
                {
                    error = "A assistente não respondeu. Tente de novo.";
                }
            }
            catch (AssistantFailure failure)
            {
                error = failure.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A atendente fechou a janela no meio da resposta: o que já chegou fica guardado.
            }

            AssistantMessage? answered = null;
            if (answer.Length > 0)
            {
                answered = new AssistantMessage
                {
                    DeviceId = device.Id,
                    ConversationId = conversationId,
                    Role = AssistantRole.Assistant,
                    Text = answer.ToString(),
                    At = time.GetUtcNow().UtcDateTime,
                };
                db.AssistantMessages.Add(answered);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            if (!ct.IsCancellationRequested)
            {
                object final = error is not null || answered is null
                    ? new { error = error ?? "A assistente não respondeu. Tente de novo." }
                    : new { done = true, id = answered.Id, at = answered.At };
                await SendAsync(context, final, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Conexão encerrada pelo terminal.
        }
        finally
        {
            Busy.TryRemove(device.Id, out _);
        }

        return Results.Empty;
    }

    /// <summary>A base ligada cabe inteira nas instruções? Senão, a IA recebe o índice e abre o que precisar.</summary>
    public static bool FitsInline(long enabledChars) => enabledChars <= KnowledgeLimits.InlineBudget;

    private static async Task<AssistantRequest> BuildRequestAsync(
        PontualDb db, Device device, string apiKey, Guid conversationId, DateTime now, Func<string, Task> onStatus, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var scripts = KnowledgeText.Ordered(await db.AssistantScripts.AsNoTracking().Where(s => s.Enabled && s.Content != "").ToListAsync(ct));
        var inline = FitsInline(scripts.Sum(s => (long)s.Content.Length));
        var general = settings.GetValueOrDefault(TextSetting.AssistantInstructions, "");
        // A assistente sabe o que está ligado na Central, para não negar o que de fato acontece.
        var about = AssistantPrompt.About(
            settings.GetValueOrDefault(TextSetting.AssistantScreen, "1") != "0",
            settings.GetValueOrDefault(TextSetting.AssistantWhatsAppLearning, "1") != "0");
        var profile = await db.AssistantProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == device.Id, ct);
        var learnedText = await LearnedAsync(db, device, settings, ct);

        var recent = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.DeviceId == device.Id && m.ConversationId == conversationId)
            .OrderByDescending(m => m.Id)
            .Take(ContextTurns)
            .ToListAsync(ct);
        recent.Reverse();

        return new AssistantRequest(
            apiKey,
            ModelOf(settings),
            inline ? AssistantPrompt.Shared(general, scripts, about) : AssistantPrompt.Index(general, scripts, about),
            AssistantPrompt.Personal(device, profile, now, learnedText),
            Turns(recent),
            inline ? null : new KnowledgeTools(scripts),
            inline ? null : onStatus);
    }

    /// <summary>
    /// O que se aprendeu sobre esta atendente, em texto para as instruções: o perfil da análise desta máquina
    /// somado ao importado do histórico de atendimentos dela. Vai só para ela; cada uma tem o seu.
    /// </summary>
    internal static async Task<string> LearnedAsync(PontualDb db, Device device, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var learned = await db.AttendantInsights.AsNoTracking()
            .Where(i => i.DeviceId == device.Id).Select(i => i.ProfileJson).FirstOrDefaultAsync(ct);
        var history = AssistantPrompt.Learned(AssistantPrompt.ImportedProfile(settings, device.AttendantName));
        var text = AssistantPrompt.Learned(learned);
        if (history.Length > 0)
        {
            text = text.Length == 0 ? history : text + "\n\nDo histórico de atendimentos dela com clientes:\n" + history;
        }

        return text;
    }

    /// <summary>
    /// A API exige que a conversa comece pela atendente e alterne os lados. Uma pergunta que ficou sem
    /// resposta (falha de rede, por exemplo) é juntada à seguinte.
    /// </summary>
    internal static List<AssistantTurn> Turns(IEnumerable<AssistantMessage> messages)
    {
        var turns = new List<AssistantTurn>();
        foreach (var message in messages)
        {
            if (turns.Count == 0 && message.Role != AssistantRole.User)
            {
                continue;
            }

            if (turns.Count > 0 && turns[^1].Role == message.Role)
            {
                turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n\n" + message.Text };
            }
            else
            {
                turns.Add(new AssistantTurn(message.Role, message.Text));
            }
        }

        return turns;
    }

    private static async Task SendAsync(HttpContext context, object payload, CancellationToken ct)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload, Web) + "\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    // ------------------------------------------------------------------- central

    private static async Task<IResult> OverviewAsync(PontualDb db, IConfiguration configuration, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var hasStoredKey = settings.TryGetValue(TextSetting.AssistantApiKey, out var stored) && AssistantSecrets.Unprotect(stored) is { Length: > 0 };
        var hasKey = hasStoredKey || !string.IsNullOrWhiteSpace(configuration["Assistant:ApiKey"]);

        // Esta seção cuida só dos roteiros; documentos e demais conhecimentos ficam na Central Neural.
        var scripts = await db.AssistantScripts.AsNoTracking()
            .Where(s => s.Kind == KnowledgeKind.Script)
            .OrderBy(s => s.Title)
            .Select(s => new { id = s.Id, title = s.Title, enabled = s.Enabled, chars = s.Content.Length, updatedAt = s.UpdatedAt, updatedBy = s.UpdatedByName })
            .ToListAsync(ct);

        var profiles = await db.AssistantProfiles.AsNoTracking().ToDictionaryAsync(p => p.DeviceId, ct);
        var usage = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.Role == AssistantRole.User)
            .GroupBy(m => m.DeviceId)
            .Select(g => new { DeviceId = g.Key, Questions = g.Count(), LastAt = g.Max(m => m.At) })
            .ToDictionaryAsync(g => g.DeviceId, ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.Approved).OrderBy(d => d.AttendantName).ThenBy(d => d.Hostname).ToListAsync(ct);

        return Results.Ok(new
        {
            settings = new
            {
                hasKey,
                keyFromServerConfig = hasKey && !hasStoredKey,
                model = ModelOf(settings),
                models = Models.Select(m => new { id = m.Id, label = m.Label }),
                instructions = settings.GetValueOrDefault(TextSetting.AssistantInstructions, ""),
                screenContext = settings.GetValueOrDefault(TextSetting.AssistantScreen, "1") != "0",
            },
            limits = new { script = MaxScript, allScripts = MaxAllScripts, profileInstructions = MaxProfileInstructions, generalInstructions = MaxGeneralInstructions },
            scripts,
            profiles = devices.Select(d =>
            {
                profiles.TryGetValue(d.Id, out var profile);
                usage.TryGetValue(d.Id, out var used);
                return new
                {
                    deviceId = d.Id,
                    attendantName = d.AttendantName,
                    hostname = d.Hostname,
                    enabled = profile?.Enabled ?? true,
                    instructions = profile?.Instructions ?? "",
                    questions = used?.Questions ?? 0,
                    lastAt = used?.LastAt,
                };
            }),
        });
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<SettingsBody>();
        var changes = new List<string>();

        if (body.ApiKey is not null || body.RemoveKey == true)
        {
            // A chave dá acesso a uma conta paga: só quem gerencia chaves mexe nela.
            context.Demand(Permission.KeysManage);
            if (body.RemoveKey == true)
            {
                await SetAsync(db, TextSetting.AssistantApiKey, "", ct);
                changes.Add("removeu a chave da IA");
            }
            else
            {
                var key = Input.Clean(body.ApiKey);
                if (key.Length is < 20 or > 300 || key.Any(char.IsWhiteSpace))
                {
                    throw ApiError.BadRequest("A chave da IA não parece válida. Copie a chave inteira, sem espaços.");
                }

                await SetAsync(db, TextSetting.AssistantApiKey, AssistantSecrets.Protect(key), ct);
                changes.Add("cadastrou a chave da IA");
            }
        }

        if (body.Model is not null)
        {
            if (Models.All(m => m.Id != body.Model))
            {
                throw ApiError.BadRequest("Modelo de IA inválido.");
            }

            if (await SetAsync(db, TextSetting.AssistantModel, body.Model, ct))
            {
                changes.Add($"trocou o modelo da assistente para {body.Model}");
            }
        }

        if (body.Instructions is not null)
        {
            var instructions = Multiline(body.Instructions);
            if (instructions.Length > MaxGeneralInstructions)
            {
                throw ApiError.BadRequest($"As orientações gerais aceitam no máximo {MaxGeneralInstructions} caracteres.");
            }

            if (await SetAsync(db, TextSetting.AssistantInstructions, instructions, ct))
            {
                changes.Add("alterou as orientações gerais da assistente");
            }
        }

        if (body.ScreenContext is { } screenContext && await SetAsync(db, TextSetting.AssistantScreen, screenContext ? "1" : "0", ct))
        {
            changes.Add(screenContext
                ? "permitiu que a assistente veja a tela e o WhatsApp das atendentes"
                : "proibiu a assistente de ver a tela e o WhatsApp das atendentes");
        }

        if (changes.Count > 0)
        {
            // Nem a chave nem o texto das orientações entram na auditoria; só o fato.
            await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.settings", $"{admin.Name} {string.Join(" e ", changes)}"), cancellationToken: ct);
        }

        return Results.NoContent();
    }

    /// <summary>Faz uma pergunta mínima à IA para confirmar que a chave e o modelo funcionam.</summary>
    private static async Task<IResult> TestAsync(PontualDb db, IConfiguration configuration, IAssistantModel model, CancellationToken ct)
    {
        var apiKey = await AssistantSecrets.ReadKeyAsync(db, configuration, ct)
            ?? throw ApiError.Conflict("Cadastre a chave da IA antes de testar.");
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var answer = new StringBuilder();
        try
        {
            await model.AskAsync(
                new AssistantRequest(apiKey, ModelOf(settings), "Este é um teste de conexão.", "Responda somente com a palavra: funcionando",
                    [new AssistantTurn(AssistantRole.User, "Teste de conexão.")]),
                piece => { answer.Append(piece); return Task.CompletedTask; },
                ct);
        }
        catch (AssistantFailure failure)
        {
            throw new ApiError(StatusCodes.Status502BadGateway, failure.Message);
        }

        return Results.Ok(new { ok = true, answer = Input.Truncated(answer.ToString(), 200) });
    }

    private static async Task<IResult> ScriptAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        var script = await db.AssistantScripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiError.NotFound("Roteiro não encontrado.");
        return Results.Ok(new { id = script.Id, title = script.Title, content = script.Content, enabled = script.Enabled, updatedAt = script.UpdatedAt, updatedBy = script.UpdatedByName });
    }

    private static async Task<IResult> CreateScriptAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ScriptBody>();
        if (await db.AssistantScripts.CountAsync(ct) >= MaxScripts)
        {
            throw ApiError.Conflict($"O limite é de {MaxScripts} roteiros. Junte ou remova algum antes de criar outro.");
        }

        var script = new AssistantScript
        {
            Id = Guid.NewGuid(),
            Title = Input.Required(body.Title, "o título do roteiro", 80),
            Content = ScriptContent(body.Content),
            Enabled = body.Enabled ?? true,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
            UpdatedByName = admin.Name,
        };
        await EnsureRoomAsync(db, script, ct);
        db.AssistantScripts.Add(script);

        await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.script.create",
            $"{admin.Name} cadastrou o roteiro \"{script.Title}\" da assistente", "assistant-script", script.Id), cancellationToken: ct);
        return Results.Ok(new { id = script.Id });
    }

    private static async Task<IResult> UpdateScriptAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ScriptBody>();
        var script = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Roteiro não encontrado.");

        if (body.Title is not null)
        {
            script.Title = Input.Required(body.Title, "o título do roteiro", 80);
        }

        if (body.Content is not null)
        {
            script.Content = ScriptContent(body.Content);
        }

        if (body.Enabled is { } enabled)
        {
            script.Enabled = enabled;
        }

        await EnsureRoomAsync(db, script, ct);
        script.UpdatedAt = time.GetUtcNow().UtcDateTime;
        script.UpdatedByName = admin.Name;

        await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.script.update",
            $"{admin.Name} alterou o roteiro \"{script.Title}\" da assistente{(script.Enabled ? "" : " (desligado)")}", "assistant-script", script.Id), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteScriptAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var script = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Roteiro não encontrado.");
        db.AssistantScripts.Remove(script);

        await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.script.delete",
            $"{admin.Name} removeu o roteiro \"{script.Title}\" da assistente", "assistant-script", script.Id), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveProfileAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ProfileBody>();
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");

        var profile = await db.AssistantProfiles.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (profile is null)
        {
            profile = new AssistantProfile { DeviceId = deviceId };
            db.AssistantProfiles.Add(profile);
        }

        var changes = new List<string>();
        if (body.Instructions is not null)
        {
            var instructions = Multiline(body.Instructions);
            if (instructions.Length > MaxProfileInstructions)
            {
                throw ApiError.BadRequest($"As orientações da atendente aceitam no máximo {MaxProfileInstructions} caracteres.");
            }

            if (instructions != profile.Instructions)
            {
                profile.Instructions = instructions;
                changes.Add($"alterou as orientações da assistente de {device.Label}");
            }
        }

        if (body.Enabled is { } enabled && enabled != profile.Enabled)
        {
            profile.Enabled = enabled;
            changes.Add($"{(enabled ? "ligou" : "desligou")} a assistente de {device.Label}");
        }

        if (changes.Count > 0)
        {
            profile.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.profile",
                $"{admin.Name} {string.Join(" e ", changes)}", "device", device.Id), cancellationToken: ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ConversationsAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");

        var messages = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.DeviceId == deviceId)
            .OrderByDescending(m => m.Id)
            .Take(ConversationsShown)
            .ToListAsync(ct);
        messages.Reverse();

        // Ler a conversa de uma pessoa é leitura de uso individual: fica registrado quem leu.
        await audit.SaveAsync(db, AuditInfo.By(admin, "assistant.read",
            $"{admin.Name} leu as conversas de {device.Label} com a assistente", "device", device.Id), cancellationToken: ct);

        return Results.Ok(messages.Select(MessageDto));
    }

    // ------------------------------------------------------------------ utilidades

    private static object MessageDto(AssistantMessage m) => new { id = m.Id, role = m.Role, text = m.Text, at = m.At, conversationId = m.ConversationId, context = m.Context };

    internal static string ModelOf(Dictionary<string, string> settings) =>
        settings.TryGetValue(TextSetting.AssistantModel, out var model) && Models.Any(m => m.Id == model) ? model : DefaultModel;

    /// <summary>Grava a configuração; devolve se o valor mudou.</summary>
    private static async Task<bool> SetAsync(PontualDb db, string key, string value, CancellationToken ct)
    {
        var setting = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.TextSettings.Add(new TextSetting { Key = key, Value = value });
            return value.Length > 0;
        }

        if (setting.Value == value)
        {
            return false;
        }

        setting.Value = value;
        return true;
    }

    private static string ScriptContent(string? raw)
    {
        var content = Multiline(raw);
        if (content.Length == 0)
        {
            throw ApiError.BadRequest("Cole o texto do roteiro.");
        }

        // Nada é cortado em silêncio: um roteiro pela metade faria a assistente responder errado.
        if (content.Length > MaxScript)
        {
            throw ApiError.BadRequest($"O roteiro tem {content.Length} caracteres; o máximo por roteiro é {MaxScript}. Divida em duas partes (por exemplo \"Banco X - parte 1\").");
        }

        return content;
    }

    /// <summary>A base ligada tem um teto: ela vai inteira para a IA enquanto couber, e como índice depois disso.</summary>
    internal static async Task EnsureRoomAsync(PontualDb db, AssistantScript script, CancellationToken ct)
    {
        if (!script.Enabled)
        {
            return;
        }

        var others = await db.AssistantScripts.AsNoTracking()
            .Where(s => s.Enabled && s.Id != script.Id)
            .SumAsync(s => s.Content.Length, ct);
        if (others + script.Content.Length > MaxAllScripts)
        {
            throw ApiError.Conflict($"Os roteiros ligados passariam de {MaxAllScripts} caracteres no total. Desligue ou enxugue algum roteiro antes.");
        }
    }

    /// <summary>
    /// Como <see cref="Input.Clean"/>, mas preserva quebras de linha e tabulações (roteiros e perguntas têm
    /// várias linhas). Tira os demais caracteres de controle e surrogates soltos.
    /// </summary>
    internal static string Multiline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value.ReplaceLineEndings("\n");
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (!char.IsSurrogate(c) && (c is '\n' or '\t' || !char.IsControl(c)))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}
