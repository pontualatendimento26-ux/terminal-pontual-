using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Clientes do CRM (ver <see cref="CrmAgent"/>). O CRM avisa cada mensagem no webhook; o <see cref="CrmWorker"/>
/// espera o cliente parar de digitar e pede a resposta à IA; a resposta aparece na Central Neural e só vai ao
/// cliente quando alguém aprova. Nada é enviado sozinho.
/// </summary>
public static class CrmEndpoints
{
    public const string WebhookRateLimit = "crm-webhook";
    private static readonly Permission Access = Permission.ConfigEdit;

    private sealed record SettingsBody(string? Url, string? User, string? Password, bool? Enabled);
    private sealed record ApproveBody(string? Message);

    public static void Map(RouteGroupBuilder api)
    {
        // O CRM chama sem login: quem prova a origem é o segredo no endereço.
        api.MapPost("/crm/webhook/{key}", WebhookAsync).RequireRateLimiting(WebhookRateLimit);
        api.MapPost("/crm/webhook/{key}/{**rest}", WebhookAsync).RequireRateLimiting(WebhookRateLimit);

        api.MapGet("/neural/crm", OverviewAsync).Panel(Access);
        api.MapPut("/neural/crm/settings", SaveSettingsAsync).Panel(Access);
        api.MapPost("/neural/crm/test", TestAsync).Panel(Access);
        api.MapPost("/neural/crm/replies/{id:guid}/approve", ApproveAsync).Panel(Access);
        api.MapPost("/neural/crm/replies/{id:guid}/reject", RejectAsync).Panel(Access);
        api.MapPost("/neural/crm/replies/{id:guid}/human", HumanAsync).Panel(Access);
    }

    // ------------------------------------------------------------------ webhook

    private static async Task<IResult> WebhookAsync(string key, HttpContext context, PontualDb db, CrmQueue queue, TimeProvider time, CancellationToken ct)
    {
        var stored = await db.TextSettings.AsNoTracking().Where(s => s.Key == TextSetting.CrmWebhookKey).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";
        if (stored.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(stored)))
        {
            return Results.Json(new { success = false, message = "Endereço desconhecido." }, statusCode: StatusCodes.Status404NotFound);
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        var parsed = CrmAgent.Parse(body);
        var crmEvent = new CrmEvent
        {
            At = time.GetUtcNow().UtcDateTime,
            ConversationId = parsed.ConversationId,
            Contact = parsed.Contact,
            Phone = parsed.Phone,
            Author = parsed.Author,
            Text = parsed.Text,
            Body = body.Length <= CrmAgent.MaxBody ? body : body[..CrmAgent.MaxBody],
        };
        db.CrmEvents.Add(crmEvent);
        await db.SaveChangesAsync(ct);

        if (parsed.ConversationId.Length > 0 && parsed.Author == CrmAuthor.Client && parsed.Text.Length > 0)
        {
            queue.Signal(parsed.ConversationId, crmEvent.At);
        }

        return Results.Ok(new { success = true });
    }

    // ------------------------------------------------------------------ escrever a resposta

    /// <summary>
    /// Escreve a resposta da IA para a conversa, se há mensagem do cliente ainda não tratada. Uma resposta
    /// anterior que ainda esperava aprovação fica velha e sai da fila.
    /// </summary>
    public static async Task WriteReplyAsync(IServiceProvider services, string conversationId, CancellationToken ct)
    {
        var db = services.GetRequiredService<PontualDb>();
        var model = services.GetRequiredService<IAssistantModel>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        if (settings.GetValueOrDefault(TextSetting.CrmEnabled, "") != "1")
        {
            return;
        }

        var events = await db.CrmEvents.AsNoTracking().Where(e => e.ConversationId == conversationId)
            .OrderByDescending(e => e.Id).Take(CrmAgent.TranscriptEvents).ToListAsync(ct);
        events.Reverse();
        var lastClient = events.LastOrDefault(e => e.Author == CrmAuthor.Client && e.Text.Length > 0);
        if (lastClient is null || await db.CrmReplies.AsNoTracking().AnyAsync(
                r => r.ConversationId == conversationId && r.LastEventId >= lastClient.Id && r.Status != CrmReplyStatus.Outdated, ct))
        {
            return;
        }

        // O que a Pontual já mandou por aqui entra na conversa mesmo que o CRM não avise as mensagens enviadas.
        var sent = await db.CrmReplies.AsNoTracking().Where(r => r.ConversationId == conversationId && r.Status == CrmReplyStatus.Sent && r.SentAt != null)
            .Select(r => new { r.SentAt, r.Reply }).ToListAsync(ct);
        var lines = events.Where(e => e.Text.Length > 0).Select(e => new CrmAgent.Line(e.At, e.Author, e.Text)).ToList();
        lines.AddRange(sent.Where(s => !lines.Any(l => l.Author == CrmAuthor.Team && l.Text == s.Reply)).Select(s => new CrmAgent.Line(s.SentAt!.Value, CrmAuthor.Team, s.Reply)));
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        var lastTeam = lines.FindLastIndex(l => l.Author == CrmAuthor.Team);
        var incoming = string.Join('\n', lines.Skip(lastTeam + 1).Where(l => l.Author == CrmAuthor.Client).Select(l => l.Text));
        if (incoming.Length == 0)
        {
            return;
        }

        var transcript = CrmAgent.Transcript(lines);
        var who = events.LastOrDefault(e => e.Contact.Length > 0)?.Contact ?? "";
        var phone = events.LastOrDefault(e => e.Phone.Length > 0)?.Phone ?? "";

        var older = await db.CrmReplies.Where(r => r.ConversationId == conversationId && (r.Status == CrmReplyStatus.Pending || r.Status == CrmReplyStatus.Failed)).ToListAsync(ct);
        foreach (var old in older)
        {
            old.Status = CrmReplyStatus.Outdated;
            old.UpdatedAt = now;
        }

        var reply = new CrmReply
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Contact = who, Phone = phone, Incoming = incoming, Transcript = transcript,
            LastEventId = lastClient.Id, CreatedAt = now, UpdatedAt = now, Status = CrmReplyStatus.Pending,
        };

        var apiKey = await AssistantSecrets.ReadKeyAsync(db, configuration, ct);
        if (apiKey is null)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = "A IA está sem chave: cadastre em Central do TI → Assistente. Você pode escrever a resposta à mão e enviar.";
        }
        else
        {
            try
            {
                var (cached, found) = await InstructionsAsync(db, settings, incoming, ct);
                var input = new StringBuilder()
                    .Append("<cliente>").Append(who.Length > 0 ? who : "sem nome").Append(phone.Length > 0 ? $" ({phone})" : "").Append("</cliente>\n\n")
                    .Append("<conversa>\n").Append(transcript).Append("\n</conversa>\n\n")
                    .Append("<mensagens_novas_do_cliente>\n").Append(incoming).Append("\n</mensagens_novas_do_cliente>");
                var json = await model.AskJsonAsync(new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(settings), found, input.ToString(),
                    CrmAgent.Schema, cached, new AssistantUsageTag(UsageKind.Crm)), ct);
                var decision = CrmAgent.ParseDecision(json);
                reply.Decision = decision.Action;
                reply.Reply = decision.Message;
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
        }

        db.CrmReplies.Add(reply);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A base (sem o que é só da equipe) e as regras do CRM, em cache; os trechos achados, fora dele.</summary>
    private static async Task<(IReadOnlyList<string> Cached, string Found)> InstructionsAsync(
        PontualDb db, Dictionary<string, string> settings, string incoming, CancellationToken ct)
    {
        var scripts = KnowledgeText.Ordered(await db.AssistantScripts.AsNoTracking()
            .Where(s => s.Enabled && !s.StaffOnly && s.Content != "").ToListAsync(ct));
        var general = settings.GetValueOrDefault(TextSetting.AssistantInstructions, "");
        string shared;
        var found = "";
        if (AssistantEndpoints.FitsInline(scripts.Sum(s => (long)s.Content.Length)))
        {
            shared = AssistantPrompt.Shared(general, scripts);
        }
        else
        {
            shared = AssistantPrompt.Index(general, scripts);
            var hits = new StringBuilder("Trechos da base que tratam do que o cliente escreveu:\n");
            foreach (var hit in KnowledgeText.Search(scripts, incoming, 6, 400))
            {
                var parts = KnowledgeText.Parts(hit.Item.Content);
                hits.Append(AssistantPrompt.Render(hit.Item, parts[hit.Part - 1], parts.Count > 1 ? $"parte {hit.Part} de {parts.Count}" : null)).Append('\n');
            }

            found = hits.ToString();
        }

        return ([shared, CrmAgent.Rules], found);
    }

    // ------------------------------------------------------------------ Central Neural

    private static async Task<IResult> OverviewAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("crm.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var key = settings.GetValueOrDefault(TextSetting.CrmWebhookKey, "");
        var pending = await db.CrmReplies.AsNoTracking()
            .Where(r => r.Status == CrmReplyStatus.Pending || r.Status == CrmReplyStatus.Failed)
            .OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var recent = await db.CrmReplies.AsNoTracking()
            .Where(r => r.Status != CrmReplyStatus.Pending && r.Status != CrmReplyStatus.Failed)
            .OrderByDescending(r => r.UpdatedAt).Take(40).ToListAsync(ct);
        var events = await db.CrmEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(15)
            .Select(e => new { id = e.Id, at = e.At, conversationId = e.ConversationId, contact = e.Contact, phone = e.Phone, author = e.Author, text = e.Text, body = e.Body })
            .ToListAsync(ct);
        return Results.Ok(new
        {
            enabled = settings.GetValueOrDefault(TextSetting.CrmEnabled, "") == "1",
            url = settings.GetValueOrDefault(TextSetting.CrmUrl, ""),
            user = settings.GetValueOrDefault(TextSetting.CrmUser, ""),
            hasPassword = settings.GetValueOrDefault(TextSetting.CrmPassword, "").Length > 0,
            webhookUrl = key.Length > 0 ? $"{PublicBase(context)}/api/v1/crm/webhook/{key}" : "",
            pending = pending.Select(Dto),
            recent = recent.Select(Dto),
            events,
        });
    }

    /// <summary>
    /// O endereço desta Central visto da rede. Aberta como "localhost", a tela mostraria um endereço que o CRM
    /// (outro computador) não alcança; aí entra o IP deste computador na rede local.
    /// </summary>
    private static string PublicBase(HttpContext context)
    {
        var host = context.Request.Host;
        var name = host.Host;
        if (name is "localhost" or "127.0.0.1" or "::1" or "[::1]")
        {
            var lan = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.ToString() is var s
                    && (s.StartsWith("192.168.", StringComparison.Ordinal) || s.StartsWith("10.", StringComparison.Ordinal) || s.StartsWith("172.", StringComparison.Ordinal)));
            if (lan is not null)
            {
                name = lan.ToString();
            }
        }

        return $"{context.Request.Scheme}://{name}{(host.Port is { } port ? $":{port}" : "")}";
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<SettingsBody>();
        var url = Input.Truncated(body.Url, 200).TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw ApiError.BadRequest("O endereço do CRM precisa começar com http:// ou https://.");
        }

        await SetAsync(db, TextSetting.CrmUrl, url, ct);
        await SetAsync(db, TextSetting.CrmUser, Input.Truncated(body.User, 80), ct);
        if (!string.IsNullOrEmpty(body.Password))
        {
            await SetAsync(db, TextSetting.CrmPassword, AssistantSecrets.Protect(body.Password.Trim()), ct);
        }

        if (!await db.TextSettings.AnyAsync(s => s.Key == TextSetting.CrmWebhookKey && s.Value != "", ct))
        {
            await SetAsync(db, TextSetting.CrmWebhookKey, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), ct);
        }

        var enabled = body.Enabled == true;
        await SetAsync(db, TextSetting.CrmEnabled, enabled ? "1" : "0", ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.settings",
            $"{admin.Name} salvou a ligação com o CRM ({(enabled ? "IA escrevendo respostas" : "IA desligada")})"), cancellationToken: ct);
        return await OverviewAsync(context, db, ct);
    }

    private static async Task SetAsync(PontualDb db, string key, string value, CancellationToken ct)
    {
        var row = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.TextSettings.Add(new TextSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<IResult> TestAsync(PontualDb db, CrmClient crm, CancellationToken ct)
    {
        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.BadRequest("Preencha o endereço, o usuário e a senha do agente e salve antes de testar.");
        try
        {
            var stages = await crm.TestAsync(access, ct);
            return Results.Ok(new { ok = true, message = $"Ligação com o CRM funcionando. O funil do agente tem {stages} etapa(s)." });
        }
        catch (CrmFailure failure)
        {
            throw ApiError.Conflict(failure.Message);
        }
    }

    private static async Task<CrmReply> OpenAsync(PontualDb db, Guid id, CancellationToken ct)
    {
        var reply = await db.CrmReplies.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiError.NotFound("Resposta não encontrada.");
        if (reply.Status is not (CrmReplyStatus.Pending or CrmReplyStatus.Failed))
        {
            throw ApiError.Conflict("Esta resposta não está mais esperando aprovação.");
        }

        return reply;
    }

    /// <summary>O cliente escreveu de novo depois desta resposta? Então ela está velha: sai da fila e a IA escreve outra.</summary>
    private static async Task RefuseIfOutdatedAsync(PontualDb db, CrmQueue queue, CrmReply reply, DateTime now, CancellationToken ct)
    {
        var newer = await db.CrmEvents.AsNoTracking()
            .Where(e => e.ConversationId == reply.ConversationId && e.Author == CrmAuthor.Client && e.Id > reply.LastEventId && e.Text != "")
            .OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        if (newer is null)
        {
            return;
        }

        reply.Status = CrmReplyStatus.Outdated;
        reply.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        queue.Signal(reply.ConversationId, newer.At);
        throw ApiError.Conflict("O cliente mandou mensagem nova depois desta resposta. Nada foi enviado: a IA está escrevendo outra, em instantes aparece aqui.");
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpContext context, PontualDb db, CrmClient crm, CrmQueue queue, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ApproveBody>();
        var reply = await OpenAsync(db, id, ct);
        var message = AssistantEndpoints.Multiline(body.Message ?? reply.Reply);
        if (message.Length == 0)
        {
            throw ApiError.BadRequest("Escreva a mensagem que vai para o cliente.");
        }

        if (message.Length > CrmAgent.MaxReply)
        {
            throw ApiError.BadRequest($"A mensagem aceita no máximo {CrmAgent.MaxReply} caracteres.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await RefuseIfOutdatedAsync(db, queue, reply, now, ct);
        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada. Preencha no topo desta tela.");

        reply.Reply = message;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        try
        {
            await crm.SendTextAsync(access, reply.ConversationId, message, ct);
        }
        catch (CrmFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = failure.Message;
            await db.SaveChangesAsync(ct);
            throw ApiError.Conflict($"Não foi enviada. {failure.Message}");
        }

        reply.Status = CrmReplyStatus.Sent;
        reply.Error = "";
        reply.SentAt = now;
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.approve",
            $"{admin.Name} aprovou e enviou uma resposta da IA para um cliente do CRM ({Label(reply)})"), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    private static async Task<IResult> RejectAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var reply = await OpenAsync(db, id, ct);
        reply.Status = CrmReplyStatus.Rejected;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.reject",
            $"{admin.Name} recusou uma resposta da IA para um cliente do CRM ({Label(reply)})"), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    /// <summary>Passa a conversa para uma atendente no CRM; se houver texto, ele vai ao cliente antes.</summary>
    private static async Task<IResult> HumanAsync(Guid id, HttpContext context, PontualDb db, CrmClient crm, CrmQueue queue, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ApproveBody>();
        var reply = await OpenAsync(db, id, ct);
        var message = AssistantEndpoints.Multiline(body.Message ?? "");
        if (message.Length > CrmAgent.MaxReply)
        {
            throw ApiError.BadRequest($"A mensagem aceita no máximo {CrmAgent.MaxReply} caracteres.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await RefuseIfOutdatedAsync(db, queue, reply, now, ct);
        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada. Preencha no topo desta tela.");

        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        string attendant;
        try
        {
            if (message.Length > 0)
            {
                await crm.SendTextAsync(access, reply.ConversationId, message, ct);
                reply.Reply = message;
                reply.SentAt = now;
            }

            var reason = reply.Reason.Length > 0 ? reply.Reason : "A IA da Pontual passou a conversa para uma atendente.";
            attendant = await crm.HandOverAsync(access, reply.ConversationId, Input.Truncated(reason, 300), ct);
        }
        catch (CrmFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = failure.Message;
            await db.SaveChangesAsync(ct);
            throw ApiError.Conflict($"Não deu para passar a conversa. {failure.Message}");
        }

        reply.Status = CrmReplyStatus.Human;
        reply.Error = attendant.Length > 0 ? $"Passada para {attendant}." : "";
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.human",
            $"{admin.Name} passou um cliente do CRM para atendente ({Label(reply)}){(attendant.Length > 0 ? $": {attendant}" : "")}"), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    private static string Label(CrmReply r) => r.Contact.Length > 0 ? r.Contact : r.Phone.Length > 0 ? r.Phone : $"conversa {r.ConversationId}";

    private static object Dto(CrmReply r) => new
    {
        id = r.Id, conversationId = r.ConversationId, contact = r.Contact, phone = r.Phone, incoming = r.Incoming, transcript = r.Transcript,
        reply = r.Reply, decision = r.Decision, reason = r.Reason, status = r.Status, error = r.Error, reviewedBy = r.ReviewedByName,
        createdAt = r.CreatedAt, updatedAt = r.UpdatedAt, sentAt = r.SentAt,
    };
}

/// <summary>Conversas com mensagem nova de cliente, esperando o <see cref="CrmWorker"/>.</summary>
public sealed class CrmQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, DateTime> _lastMessage = new();

    public ChannelReader<string> Reader => _channel.Reader;

    public void Signal(string conversationId, DateTime at)
    {
        _lastMessage[conversationId] = at;
        _channel.Writer.TryWrite(conversationId);
    }

    /// <summary>Hora da última mensagem do cliente nesta conversa, para esperar ele parar de digitar.</summary>
    public DateTime LastMessage(string conversationId) => _lastMessage.GetValueOrDefault(conversationId);
}

/// <summary>
/// Escreve as respostas: para cada conversa avisada, espera alguns segundos sem mensagem nova (o cliente costuma
/// mandar picado) e pede uma resposta à IA. No máximo três conversas ao mesmo tempo. Também apaga o que passou de 30 dias.
/// </summary>
public sealed class CrmWorker(IServiceScopeFactory scopes, CrmQueue queue, TimeProvider time, ILogger<CrmWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly ConcurrentDictionary<string, byte> _waiting = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = CleanupLoopAsync(stoppingToken);
        await foreach (var conversationId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (_waiting.TryAdd(conversationId, 0))
            {
                _ = HandleAsync(conversationId, stoppingToken);
            }
        }
    }

    private async Task HandleAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var quietAt = queue.LastMessage(conversationId) + CrmAgent.Quiet;
                var wait = quietAt - time.GetUtcNow().UtcDateTime;
                if (wait <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(wait, time, ct);
            }

            _waiting.TryRemove(conversationId, out _);
            await _slots.WaitAsync(ct);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await CrmEndpoints.WriteReplyAsync(scope.ServiceProvider, conversationId, ct);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao escrever a resposta do CRM para a conversa {Conversation}", conversationId);
        }
        finally
        {
            _waiting.TryRemove(conversationId, out _);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
                var old = time.GetUtcNow().UtcDateTime - CrmAgent.Keep;
                await db.CrmEvents.Where(e => e.At < old).ExecuteDeleteAsync(ct);
                await db.CrmReplies.Where(r => r.UpdatedAt < old).ExecuteDeleteAsync(ct);
                await Task.Delay(TimeSpan.FromHours(6), time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao limpar os registros antigos do CRM");
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
