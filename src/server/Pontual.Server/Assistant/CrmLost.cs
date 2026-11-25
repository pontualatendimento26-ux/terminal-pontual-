using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>
/// Clientes perdidos do CRM: no funil Atendimento Portabilidade, conversas em que o cliente escreveu por último e
/// ninguém respondeu há mais de um dia (olhando até 15 dias para trás). A IA lê cada uma, decide se o cliente
/// espera mesmo resposta e escreve no jeito da atendente dona do cliente (o "Responsável" no CRM). A resposta
/// espera aprovação em Mensagens para aprovar e sai pela tela de conversa do CRM, na linha do cliente.
/// </summary>
public static class CrmLost
{
    public const string Kind = "perdido";
    /// <summary>O funil Atendimento Portabilidade.</summary>
    public const string Funnel = "4";
    /// <summary>Sem resposta há menos que isso ainda não é perdido: a atendente pode só estar ocupada.</summary>
    public static readonly TimeSpan MinWait = TimeSpan.FromHours(24);
    /// <summary>Até onde a busca olha para trás.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(15);
    /// <summary>No máximo tantas conversas lidas pela IA por busca: segura o gasto.</summary>
    public const int MaxCalls = 25;
    private const int MaxPages = 40;
    private const int TranscriptMessages = 40;

    private static readonly HashSet<string> SystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "notification_template", "notification", "e2e_notification", "gp2", "call_log", "ciphertext", "revoked", "comentario", "lembrete", "protocol",
    };

    public sealed record Result(int Checked, int Created, string Note);

    /// <summary>Uma fala do histórico do CRM.</summary>
    public sealed record Message(DateTime At, bool FromMe, string Who, string Text, string WppId);

    public const string Rules =
        """
        MODO CLIENTE SEM RESPOSTA

        Este cliente mandou mensagem e ficou sem resposta da Pontual há bastante tempo (veja as datas na conversa). Você vai retomar a conversa no lugar da atendente dona do cliente, indicada em <atendente>.

        - Escreva no jeito dela: veja como ela escreve nas falas marcadas com o nome dela na conversa e nos exemplos aprovados (tamanho, cumprimento, emojis, forma de tratar o cliente). Não assine e não diga o nome dela se ela não costuma dizer.
        - Comece retomando com naturalidade e um pedido de desculpas curto pela demora, e responda de verdade o que o cliente perguntou ou conduza para o próximo passo (por exemplo, pedir o CPF ou o contracheque para simular).
        - "nada" quando o cliente não espera resposta: agradeceu, mandou "ok", figurinha, encerrou, disse que não quer mais, pediu para não ser chamado, ou a mensagem é automática.
        - "humano" quando só a atendente pode responder (valor, proposta ou situação de contrato que não está na conversa, reclamação, cancelamento). Em "mensagem" deixe vazio ou um texto curto de retomada; em "motivo" diga o que a atendente precisa fazer.
        """;

    /// <summary>Procura os clientes perdidos e escreve as respostas. <paramref name="force"/> roda mesmo com a busca desligada (botão da tela).</summary>
    /// <summary>Até quantos dias para trás a tela deixa escolher.</summary>
    public const int MaxDays = 60;
    /// <summary>Com datas escolhidas na tela, quem escreveu há menos que isso ainda fica de fora: a atendente pode estar respondendo.</summary>
    public static readonly TimeSpan MinWaitChosen = TimeSpan.FromHours(1);

    /// <summary>
    /// Procura os clientes perdidos e escreve as respostas. <paramref name="force"/> roda mesmo com a busca desligada (botão da tela).
    /// Sem datas: de 15 dias atrás até 24 horas atrás. Com datas (dias de Brasília, inclusive): o período escolhido, até 1 hora atrás.
    /// </summary>
    public static async Task<Result> ScanAsync(IServiceProvider services, bool force, CancellationToken ct, DateTime? fromDay = null, DateTime? toDay = null)
    {
        var db = services.GetRequiredService<PontualDb>();
        var crm = services.GetRequiredService<CrmClient>();
        var model = services.GetRequiredService<IAssistantModel>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        if (!force && settings.GetValueOrDefault(TextSetting.CrmLostEnabled, "") != "1")
        {
            return new Result(0, 0, "A busca dos clientes perdidos está desligada.");
        }

        if (await CrmClient.AccessAsync(db, ct) is not { } access)
        {
            return new Result(0, 0, "A ligação com o CRM não está configurada.");
        }

        if (await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is not { } apiKey)
        {
            return new Result(0, 0, "A IA está sem chave: cadastre em Central do TI → Assistente.");
        }

        var lines = (await crm.LinesAsync(access, ct)).Where(l => l.Funnel == Funnel).Select(l => l.Id).ToHashSet();
        if (lines.Count == 0)
        {
            return new Result(0, 0, "Nenhuma linha de WhatsApp no funil Atendimento Portabilidade.");
        }

        var users = (await crm.UsersAsync(access, ct)).ToDictionary(u => u.Id, u => u.Name);
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Date;
        var chosen = fromDay is not null || toDay is not null;
        var firstDay = (fromDay ?? today - Window).Date;
        var lastDay = (toDay ?? today).Date;
        var oldest = chosen ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(firstDay, DateTimeKind.Unspecified), AssistantPrompt.BrazilTime) : now - Window;
        var newest = chosen
            ? new[] { TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(lastDay.AddDays(1), DateTimeKind.Unspecified), AssistantPrompt.BrazilTime), now - MinWaitChosen }.Min()
            : now - MinWait;

        // Já tratados: a mesma última mensagem não é lida de novo, e conversa com resposta esperando aprovação fica como está.
        var seen = (await db.CrmReplies.AsNoTracking().Where(r => r.Kind == Kind && (r.WaitingSince == null || r.WaitingSince >= oldest))
                .Select(r => new { r.ChatId, r.BotId, r.LastWppId, r.Status }).ToListAsync(ct));
        var done = seen.Select(r => $"{r.ChatId}|{r.BotId}|{r.LastWppId}").ToHashSet();
        var open = seen.Where(r => r.Status is CrmReplyStatus.Pending or CrmReplyStatus.Failed).Select(r => $"{r.ChatId}|{r.BotId}").ToHashSet();

        var (cached, _) = (await CrmEndpoints.InstructionsAsync(db, settings, "", ct));
        var calls = 0;
        var created = 0;
        var checkedCount = 0;
        for (var page = 1; page <= MaxPages && calls < MaxCalls; page++)
        {
            var (chats, more) = await crm.WaitingChatsAsync(access, lines, firstDay, lastDay, page, ct);
            if (chats.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var reachedOld = false;
            foreach (var chat in chats.EnumerateArray())
            {
                if (calls >= MaxCalls)
                {
                    break;
                }

                var chatId = Str(chat, "chat_id");
                var lastAt = Time(Str(chat, "last_message_at"), utc: true);
                if (lastAt is { } stale && stale < oldest)
                {
                    reachedOld = true;
                    break;
                }

                if (!chat.TryGetProperty("canais", out var channels) || channels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var channel in channels.EnumerateArray())
                {
                    var botId = Str(channel, "bot_id");
                    var at = Time(Str(channel, "ts"), utc: false);
                    if (!lines.Contains(botId) || chatId.Length == 0 || at is null || at > newest || at < oldest
                        || channel.TryGetProperty("from_me", out var fromMe) && fromMe.ValueKind != JsonValueKind.False)
                    {
                        continue;
                    }

                    checkedCount++;
                    var wpp = channel.TryGetProperty("ultima", out var last) ? Str(last, "wpp") : "";
                    if (done.Contains($"{chatId}|{botId}|{wpp}") || open.Contains($"{chatId}|{botId}") || calls >= MaxCalls)
                    {
                        continue;
                    }

                    calls++;
                    var owner = users.GetValueOrDefault(Str(channel, "responsavel_id"), "");
                    var reply = await WriteAsync(db, crm, model, access, apiKey, settings, cached, chatId, botId, Str(chat, "name"), owner, now, ct);
                    if (reply is null)
                    {
                        continue;
                    }

                    db.CrmReplies.Add(reply);
                    await db.SaveChangesAsync(ct);
                    done.Add($"{chatId}|{botId}|{reply.LastWppId}");
                    if (reply.Status == CrmReplyStatus.Pending)
                    {
                        created++;
                        open.Add($"{chatId}|{botId}");
                    }
                }
            }

            if (reachedOld || !more)
            {
                break;
            }
        }

        var note = created > 0 ? $"{created} cliente(s) perdido(s) com resposta para aprovar."
            : checkedCount > 0 ? "Nenhum cliente perdido novo." : chosen
                ? $"Nenhuma conversa sem resposta entre {firstDay:dd/MM} e {lastDay:dd/MM} no funil."
                : "Nenhuma conversa sem resposta entre 1 e 15 dias no funil.";
        if (chosen)
        {
            note = $"De {firstDay:dd/MM} a {lastDay:dd/MM}: {note}";
        }
        if (calls >= MaxCalls)
        {
            note += $" Parou em {MaxCalls} conversas lidas para não gastar demais; a próxima busca continua.";
        }

        return new Result(checkedCount, created, note);
    }

    private static async Task<CrmReply?> WriteAsync(PontualDb db, CrmClient crm, IAssistantModel model, CrmAccess access, string apiKey,
        Dictionary<string, string> settings, IReadOnlyList<string> cached, string chatId, string botId, string name, string owner, DateTime now, CancellationToken ct)
    {
        var history = Read(await crm.MessagesAsync(access, chatId, botId, ct));
        var lastIndex = history.FindLastIndex(m => m.Text.Length > 0);
        if (lastIndex < 0 || history[lastIndex].FromMe)
        {
            return null;
        }

        var lastTeam = history.FindLastIndex(m => m.FromMe);
        var incoming = string.Join('\n', history.Skip(lastTeam + 1).Where(m => !m.FromMe).Select(m => m.Text));
        var lines = history.TakeLast(TranscriptMessages)
            .Select(m => new CrmAgent.Line(m.At, m.FromMe ? CrmAuthor.Team : CrmAuthor.Client, m.FromMe && m.Who.Length > 0 ? $"({m.Who}) {m.Text}" : m.Text));
        var transcript = CrmAgent.Transcript(lines);
        var waiting = history[lastIndex].At;
        var reply = new CrmReply
        {
            Id = Guid.NewGuid(), ConversationId = $"{chatId}|{botId}", Kind = Kind, ChatId = chatId, BotId = botId, LastWppId = history[lastIndex].WppId,
            WaitingSince = waiting, Contact = name, Phone = new string(chatId.Split('@')[0].Where(char.IsAsciiDigit).ToArray()),
            Incoming = incoming, Transcript = transcript, Attendant = owner, CreatedAt = now, UpdatedAt = now, Status = CrmReplyStatus.Pending,
        };

        var days = Math.Max(1, (int)(now - waiting).TotalDays);
        var input = new StringBuilder()
            .Append("<atendente>").Append(owner.Length > 0 ? owner : "não definida: escreva no jeito geral da equipe").Append("</atendente>\n")
            .Append("<cliente>").Append(name.Length > 0 ? name : "sem nome").Append(" — sem resposta há ").Append(days).Append(days == 1 ? " dia" : " dias").Append("</cliente>\n\n")
            .Append("<conversa>\n").Append(transcript).Append("\n</conversa>\n\n")
            .Append("<mensagens_sem_resposta>\n").Append(incoming).Append("\n</mensagens_sem_resposta>")
            .Append(await CrmEndpoints.LearnedAsync(db, owner, ct));
        try
        {
            var json = await model.AskJsonAsync(new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(settings), "", input.ToString(),
                CrmAgent.Schema, [.. cached, Rules], new AssistantUsageTag(UsageKind.Crm)), ct);
            var decision = CrmAgent.ParseDecision(json);
            reply.Decision = decision.Action;
            reply.Reply = decision.Message;
            reply.Draft = decision.Message;
            reply.Reason = Input.Truncated(decision.Reason, 400);
            if (decision.Action == CrmAgent.None)
            {
                reply.Status = CrmReplyStatus.Skipped;
            }
        }
        catch (AssistantFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = $"{failure.Message} Você pode escrever a resposta à mão e enviar.";
        }

        return reply;
    }

    /// <summary>
    /// Confere no CRM, antes de enviar, se a conversa ainda está como a IA leu. Devolve vazio se pode enviar, ou o motivo
    /// para não enviar (a atendente respondeu, ou o cliente escreveu de novo).
    /// </summary>
    public static async Task<string> ChangedAsync(CrmClient crm, CrmAccess access, CrmReply reply, CancellationToken ct)
    {
        var history = Read(await crm.MessagesAsync(access, reply.ChatId, reply.BotId, ct));
        var last = history.LastOrDefault(m => m.Text.Length > 0);
        if (last is null || last.WppId == reply.LastWppId)
        {
            return "";
        }

        return last.FromMe
            ? $"{(last.Who.Length > 0 ? last.Who : "A equipe")} já respondeu este cliente no CRM. Nada foi enviado."
            : "O cliente escreveu de novo depois desta resposta. Nada foi enviado: a próxima busca escreve outra.";
    }

    /// <summary>O histórico do CRM em falas, da mais velha para a mais nova, sem os avisos do sistema e as notas internas.</summary>
    public static List<Message> Read(JsonElement messages)
    {
        var list = new List<Message>();
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = Str(m, "type");
            var who = Str(m, "sent_by_name");
            if (SystemTypes.Contains(type) || who.Equals("Sistema", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = Plain(Str(m, "body"));
            if (Str(m, "transcription") is { Length: > 0 } transcription)
            {
                text = $"[áudio] {transcription}";
            }
            else if (type is not ("chat" or "text" or "") && (text.Length == 0 || text.StartsWith('(')))
            {
                text = text.Length > 0 && !text.StartsWith('(') ? $"[{type}] {text}" : $"[{type}]";
            }

            if (text.Length == 0)
            {
                continue;
            }

            var at = m.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.MinValue;
            var fromMe = m.TryGetProperty("is_from_me", out var f) && f.ValueKind == JsonValueKind.True;
            list.Add(new Message(at, fromMe, fromMe ? who : "", text.Length <= CrmAgent.MaxText ? text : text[..CrmAgent.MaxText], Str(m, "wpp_id")));
        }

        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    private static string Plain(string html) =>
        WebUtility(Regex.Replace(html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase), "<[^>]+>", "")).Trim();

    private static string WebUtility(string text) => System.Net.WebUtility.HtmlDecode(text);

    private static string Str(JsonElement scope, string name) =>
        scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    /// <summary>Lê a data do CRM: "last_message_at" vem em UTC (com Z); o "ts" de cada linha vem na hora de Brasília, sem fuso.</summary>
    private static DateTime? Time(string text, bool utc)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return null;
        }

        if (text.EndsWith('Z') || text.Contains('+') || utc && parsed.Kind != DateTimeKind.Unspecified)
        {
            return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), AssistantPrompt.BrazilTime);
    }
}

/// <summary>Roda a busca dos clientes perdidos a cada meia hora, das 8h às 18h (horário de Brasília), de segunda a sábado.</summary>
public sealed class CrmLostWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, AssistantPrompt.BrazilTime);
                if (local.DayOfWeek != DayOfWeek.Sunday && local.Hour is >= 8 and < 18)
                {
                    await RunAsync(scopes, time, logger, force: false, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(30), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Uma busca, uma de cada vez; grava quando rodou e o que achou para a tela.</summary>
    public static async Task<CrmLost.Result> RunAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, bool force, CancellationToken ct,
        DateTime? fromDay = null, DateTime? toDay = null)
    {
        if (!await Running.WaitAsync(0, ct))
        {
            return new CrmLost.Result(0, 0, "Já há uma busca rodando; espere ela terminar.");
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            CrmLost.Result result;
            try
            {
                result = await CrmLost.ScanAsync(scope.ServiceProvider, force, ct, fromDay, toDay);
            }
            catch (CrmFailure failure)
            {
                result = new CrmLost.Result(0, 0, $"A busca parou: {failure.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na busca dos clientes perdidos do CRM");
                result = new CrmLost.Result(0, 0, "A busca falhou por um erro interno; está no registro da Central.");
            }

            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            var value = $"{time.GetUtcNow().UtcDateTime:O}|{result.Checked}|{result.Created}|{result.Note}";
            var row = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.CrmLostLastRun, ct);
            if (row is null)
            {
                db.TextSettings.Add(new TextSetting { Key = TextSetting.CrmLostLastRun, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await db.SaveChangesAsync(ct);
            return result;
        }
        finally
        {
            Running.Release();
        }
    }
}
