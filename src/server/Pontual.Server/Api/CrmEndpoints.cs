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
    private sealed record ApproveBody(string? Message, string? UserId = null);

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
        api.MapPost("/neural/crm/replies/{id:guid}/finished", FinishedAsync).Panel(Access);
        api.MapPost("/neural/crm/replies/{id:guid}/human", HumanAsync).Panel(Access);
        api.MapGet("/neural/crm/users", UsersAsync).Panel(Access);
        api.MapPut("/neural/crm/lost", SaveLostAsync).Panel(Access);
        api.MapPost("/neural/crm/lost/scan", ScanLostAsync).Panel(Access);
        api.MapDelete("/neural/crm/lost/period", ClearPeriodAsync).Panel(Access);
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
            Sender = parsed.Sender,
            Text = parsed.Text,
            Line = parsed.Line,
            LineName = parsed.LineName,
            Funnel = parsed.Funnel,
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

        // Fora do funil de teste, a IA só escreve para clientes de uma atendente que faltou hoje (e no horário dela).
        var line = events.LastOrDefault(e => e.Line.Length > 0)?.Line ?? "";
        var funnel = events.LastOrDefault(e => e.Funnel.Length > 0)?.Funnel ?? "";
        var attendant = await AbsentAttendantAsync(db, line, now, ct);
        if (attendant is null && funnel != CrmAgent.TestFunnel)
        {
            return;
        }

        // De quem é o cliente: a que faltou ou, se não, a última pessoa da equipe que escreveu a ele no CRM.
        attendant ??= events.LastOrDefault(e => e.Author == CrmAuthor.Team && CrmAgent.IsPerson(e.Sender))?.Sender;

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
            LastEventId = lastClient.Id, Attendant = attendant ?? "", CreatedAt = now, UpdatedAt = now, Status = CrmReplyStatus.Pending,
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
                input.Append(await LearnedAsync(db, reply.Attendant, ct));
                var json = await model.AskJsonAsync(new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(settings), found, input.ToString(),
                    CrmAgent.Schema, cached, new AssistantUsageTag(UsageKind.Crm)), ct);
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
        }

        db.CrmReplies.Add(reply);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Quantas respostas aprovadas entram como exemplo a cada resposta nova.</summary>
    private const int LearnedExamples = 8;
    /// <summary>Quantas são olhadas para achar as que servem: as que passam o cliente para outra pessoa não viram exemplo.</summary>
    private const int LearnedLookup = 60;

    /// <summary>
    /// O que a IA aprendeu com as aprovações: as últimas respostas aprovadas e enviadas, primeiro as da mesma
    /// atendente, com o que a IA tinha escrito quando a pessoa editou. Vazio enquanto ninguém aprovou nada.
    /// </summary>
    public static async Task<string> LearnedAsync(PontualDb db, string attendant, CancellationToken ct)
    {
        var approved = db.CrmReplies.AsNoTracking()
            .Where(r => r.Status == CrmReplyStatus.Sent && r.ReviewedByName != "" && r.Reply != "" && r.Incoming != "");
        var mine = attendant.Length > 0
            ? Good(await approved.Where(r => r.Attendant == attendant).OrderByDescending(r => r.SentAt).Take(LearnedLookup).ToListAsync(ct), LearnedExamples)
            : [];
        var others = mine.Count < LearnedExamples
            ? Good(await approved.Where(r => r.Attendant != attendant).OrderByDescending(r => r.SentAt).Take(LearnedLookup).ToListAsync(ct), LearnedExamples - mine.Count)
            : [];
        static List<CrmReply> Good(List<CrmReply> list, int count) =>
            [.. list.Where(r => !CrmAgent.MentionsHandoff(r.Reply, r.Attendant)).Take(count)];

        if (mine.Count + others.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder("\n\n<respostas_aprovadas>\n")
            .Append("Respostas que a equipe da Pontual aprovou e enviou a clientes. Siga o tom, o tamanho e o jeito delas")
            .Append(attendant.Length > 0 ? $"; as marcadas com {attendant} são da atendente deste cliente e valem mais" : "")
            .Append(". Quando a pessoa corrigiu o texto da IA, o corrigido é o certo: evite o que ela tirou.\n");
        foreach (var r in mine.Concat(others))
        {
            text.Append("\n[").Append(r.Attendant.Length > 0 ? r.Attendant : "equipe").Append("]\n")
                .Append("Cliente: ").Append(Input.Truncated(r.Incoming, 400)).Append('\n');
            if (r.Draft.Length > 0 && r.Draft.Trim() != r.Reply.Trim())
            {
                text.Append("A IA tinha escrito: ").Append(Input.Truncated(r.Draft, 400)).Append('\n')
                    .Append("Aprovado depois de corrigido: ");
            }
            else
            {
                text.Append("Aprovado: ");
            }

            text.Append(Input.Truncated(r.Reply, 600)).Append('\n');
        }

        return text.Append("</respostas_aprovadas>").ToString();
    }

    /// <summary>O nome da atendente dona desta linha, se ela foi marcada como ausente hoje e está no horário dela; senão, nulo.</summary>
    public static async Task<string?> AbsentAttendantAsync(PontualDb db, string line, DateTime utcNow, CancellationToken ct)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var today = Coverage.Day(utcNow);
        var absent = await db.AssistantProfiles.AsNoTracking().Where(p => p.AbsentOn == today && p.CrmLines != "")
            .Join(db.Devices.AsNoTracking(), p => p.DeviceId, d => d.Id, (p, d) => new { p.CrmLines, p.CoverageSchedule, d.AttendantName, d.Hostname })
            .ToListAsync(ct);
        var owner = absent.FirstOrDefault(a => CrmAgent.HasLine(a.CrmLines, line) && Coverage.WithinHours(utcNow, a.CoverageSchedule));
        return owner is null ? null : (string.IsNullOrWhiteSpace(owner.AttendantName) ? owner.Hostname : owner.AttendantName);
    }

    /// <summary>A base (sem o que é só da equipe) e as regras do CRM, em cache; os trechos achados, fora dele.</summary>
    public static async Task<(IReadOnlyList<string> Cached, string Found)> InstructionsAsync(
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
        var waiting = db.CrmReplies.AsNoTracking().Where(r => r.Status == CrmReplyStatus.Pending || r.Status == CrmReplyStatus.Failed);
        // Com período escolhido, a lista só mostra os clientes perdidos daquelas datas; os outros continuam guardados.
        var period = CrmLost.ReadPeriod(settings.GetValueOrDefault(TextSetting.CrmLostPeriod, ""));
        var hidden = 0;
        if (period is { } days)
        {
            var (start, end) = CrmLost.Bounds(days.From, days.To);
            var total = await waiting.CountAsync(ct);
            waiting = waiting.Where(r => r.Kind != CrmLost.Kind || r.WaitingSince >= start && r.WaitingSince < end);
            hidden = total - await waiting.CountAsync(ct);
        }

        var pending = await waiting.OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var recent = await db.CrmReplies.AsNoTracking()
            .Where(r => r.Status != CrmReplyStatus.Pending && r.Status != CrmReplyStatus.Failed)
            .OrderByDescending(r => r.UpdatedAt).Take(40).ToListAsync(ct);
        var events = await db.CrmEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(15)
            .Select(e => new { id = e.Id, at = e.At, conversationId = e.ConversationId, contact = e.Contact, phone = e.Phone, author = e.Author, text = e.Text, body = e.Body })
            .ToListAsync(ct);
        // O que a IA já aprendeu: respostas aprovadas e enviadas, por atendente, e quantas foram corrigidas antes.
        var learned = (await db.CrmReplies.AsNoTracking()
                .Where(r => r.Status == CrmReplyStatus.Sent && r.ReviewedByName != "" && r.Reply != "")
                .Select(r => new { r.Attendant, r.Draft, r.Reply }).ToListAsync(ct))
            .GroupBy(r => r.Attendant)
            .Select(g => new { attendant = g.Key, approved = g.Count(), edited = g.Count(r => r.Draft.Length > 0 && r.Draft.Trim() != r.Reply.Trim()) })
            .OrderByDescending(g => g.approved).ToList();
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
            learned,
            lostEnabled = settings.GetValueOrDefault(TextSetting.CrmLostEnabled, "") == "1",
            lostLastRun = settings.GetValueOrDefault(TextSetting.CrmLostLastRun, ""),
            lostPeriod = period is { } p ? new { from = p.From.ToString("yyyy-MM-dd"), to = p.To.ToString("yyyy-MM-dd") } : null,
            lostHidden = hidden,
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
        var lost = reply.Kind == CrmLost.Kind;
        if (!lost)
        {
            await RefuseIfOutdatedAsync(db, queue, reply, now, ct);
        }

        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada. Preencha no topo desta tela.");
        if (lost)
        {
            string changed;
            try
            {
                changed = await CrmLost.ChangedAsync(crm, access, reply, ct);
            }
            catch (CrmFailure failure)
            {
                throw ApiError.Conflict($"Não deu para conferir a conversa no CRM. Nada foi enviado. {failure.Message}");
            }

            if (changed.Length > 0)
            {
                reply.Status = CrmReplyStatus.Outdated;
                reply.Error = changed;
                reply.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                throw ApiError.Conflict(changed);
            }
        }

        reply.Reply = message;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        try
        {
            if (lost)
            {
                await crm.SendChatAsync(access, reply.ChatId, reply.BotId, message, ct);
            }
            else
            {
                await crm.SendTextAsync(access, reply.ConversationId, message, ct);
            }
        }
        catch (CrmFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = failure.Message;
            await db.SaveChangesAsync(ct);
            throw ApiError.Conflict($"Não foi enviada. {failure.Message}");
        }

        reply.Status = CrmReplyStatus.Sent;
        reply.Error = await TagAsync(db, crm, access, reply, ct);
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

    /// <summary>A pessoa disse que este cliente perdido já foi finalizado: nada é enviado e a conversa vira exemplo para a IA.</summary>
    private static async Task<IResult> FinishedAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var reply = await OpenAsync(db, id, ct);
        if (reply.Kind != CrmLost.Kind)
        {
            throw ApiError.BadRequest("Só os clientes perdidos podem ser marcados como finalizados.");
        }

        reply.Status = CrmReplyStatus.Skipped;
        reply.Reason = $"{CrmLost.FinishedReason}: marcado por {admin.Name}.";
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.finished",
            $"{admin.Name} marcou como já finalizado um cliente perdido do CRM ({Label(reply)})"), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    private sealed record LostBody(bool? Enabled);

    private static async Task<IResult> SaveLostAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var enabled = (await context.ReadBodyAsync<LostBody>()).Enabled == true;
        await SetAsync(db, TextSetting.CrmLostEnabled, enabled ? "1" : "0", ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.lost",
            $"{admin.Name} {(enabled ? "ligou" : "desligou")} a busca dos clientes perdidos do CRM"), cancellationToken: ct);
        return await OverviewAsync(context, db, ct);
    }

    /// <summary>Roda a busca agora (botão da tela), mesmo fora do horário e com a busca automática desligada.</summary>
    private sealed record ScanBody(string? From, string? To);

    private static async Task<IResult> ClearPeriodAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        await SetAsync(db, TextSetting.CrmLostPeriod, "", ct);
        await db.SaveChangesAsync(ct);
        return await OverviewAsync(context, db, ct);
    }

    private static async Task<IResult> ScanLostAsync(HttpContext context, PontualDb db, IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger, CancellationToken ct)
    {
        var body = context.Request.HasJsonContentType() ? await context.ReadBodyAsync<ScanBody>() : new ScanBody(null, null);
        static DateTime? Day(string? text, string label) =>
            string.IsNullOrWhiteSpace(text) ? null
            : DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d
            : throw ApiError.BadRequest($"A data {label} não é válida.");
        var from = Day(body.From, "de início");
        var to = Day(body.To, "de fim");
        var today = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, AssistantPrompt.BrazilTime).Date;
        if (from > to)
        {
            throw ApiError.BadRequest("A data de início precisa ser antes da data de fim.");
        }

        if (from > today || to > today)
        {
            throw ApiError.BadRequest("Escolha datas até hoje.");
        }

        if ((from ?? to) is { } first && first < today.AddDays(-CrmLost.MaxDays))
        {
            throw ApiError.BadRequest($"A busca olha no máximo {CrmLost.MaxDays} dias para trás.");
        }

        // O período fica valendo para a lista e para a busca automática até alguém mostrar todas as datas.
        if (from is not null || to is not null)
        {
            from ??= today.AddDays(-15);
            to ??= today;
            await SetAsync(db, TextSetting.CrmLostPeriod, $"{from:yyyy-MM-dd}|{to:yyyy-MM-dd}", ct);
            await db.SaveChangesAsync(ct);
        }

        var result = await CrmLostWorker.RunAsync(scopes, time, logger, force: true, ct, from, to);
        return Results.Ok(new { result.Checked, result.Created, message = result.Note });
    }

    /// <summary>Os usuários do CRM, para escolher a atendente quando a Central não sabe de quem é o cliente.</summary>
    private static async Task<IResult> UsersAsync(PontualDb db, CrmClient crm, CancellationToken ct)
    {
        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada.");
        try
        {
            return Results.Ok((await crm.UsersAsync(access, ct)).Select(u => new { id = u.Id, name = u.Name }));
        }
        catch (CrmFailure failure)
        {
            throw ApiError.Conflict(failure.Message);
        }
    }

    /// <summary>Passa a conversa para a atendente dona do cliente no CRM; se houver texto, ele vai ao cliente antes.</summary>
    private static async Task<IResult> HumanAsync(Guid id, HttpContext context, PontualDb db, CrmClient crm, CrmQueue queue, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ApproveBody>();
        var reply = await OpenAsync(db, id, ct);
        if (reply.Kind == CrmLost.Kind)
        {
            throw ApiError.Conflict("Este cliente já é da atendente dele no CRM. Para deixar com ela, use Recusar: nada vai ao cliente.");
        }

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
            // Volta para a atendente que já é dona do cliente (ou a escolhida na tela). Sem saber quem é, não passa:
            // o CRM sortearia alguém e o cliente sairia da carteira dela.
            var userId = Input.Truncated(body.UserId, 20);
            if (userId.Length == 0 && reply.Attendant.Length > 0)
            {
                userId = CrmAgent.MatchUser(await crm.UsersAsync(access, ct), reply.Attendant) ?? "";
            }

            if (userId.Length == 0)
            {
                throw ApiError.Conflict(reply.Attendant.Length > 0
                    ? $"Não achei \"{reply.Attendant}\" entre os usuários do CRM. Escolha a atendente na lista e tente de novo. Nada foi enviado."
                    : "Não sei de qual atendente é este cliente. Escolha a atendente na lista e tente de novo. Nada foi enviado.");
            }

            if (message.Length > 0)
            {
                await crm.SendTextAsync(access, reply.ConversationId, message, ct);
                reply.Reply = message;
                reply.SentAt = now;
            }

            var reason = reply.Reason.Length > 0 ? reply.Reason : "A IA da Pontual passou a conversa para uma atendente.";
            attendant = await crm.HandOverAsync(access, reply.ConversationId, Input.Truncated(reason, 300), userId, ct);
        }
        catch (CrmFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = failure.Message;
            await db.SaveChangesAsync(ct);
            throw ApiError.Conflict($"Não deu para passar a conversa. {failure.Message}");
        }

        reply.Status = CrmReplyStatus.Human;
        var untagged = message.Length > 0 ? await TagAsync(db, crm, access, reply, ct) : "";
        reply.Error = string.Join(' ', new[] { attendant.Length > 0 ? $"Passada para {attendant}." : "", untagged }.Where(s => s.Length > 0));
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.human",
            $"{admin.Name} passou um cliente do CRM para atendente ({Label(reply)}){(attendant.Length > 0 ? $": {attendant}" : "")}"), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    /// <summary>
    /// Marca o cliente como atendido pela IA no CRM, depois que a mensagem saiu. A etiqueta não segura o envio:
    /// se o CRM recusar, a mensagem já foi e o aviso volta para aparecer na tela. Vazio quando deu certo.
    /// </summary>
    private static async Task<string> TagAsync(PontualDb db, CrmClient crm, CrmAccess access, CrmReply reply, CancellationToken ct)
    {
        try
        {
            if (reply.Kind == CrmLost.Kind)
            {
                await crm.TagAsync(access, reply.ChatId, reply.BotId, ct);
                return "";
            }

            var bodies = await db.CrmEvents.AsNoTracking().Where(e => e.ConversationId == reply.ConversationId)
                .OrderByDescending(e => e.Id).Take(10).Select(e => e.Body).ToListAsync(ct);
            var lineId = bodies.Select(CrmAgent.LineIdOf).FirstOrDefault(id => id.Length > 0) ?? "";
            await crm.TagAsync(access, reply.Phone, lineId, ct);
            return "";
        }
        catch (CrmFailure failure)
        {
            return $"Enviada, mas sem a etiqueta \"{CrmAgent.AiTag}\": {failure.Message}";
        }
    }

    private static string Label(CrmReply r) => r.Contact.Length > 0 ? r.Contact : r.Phone.Length > 0 ? r.Phone : $"conversa {r.ConversationId}";

    private static object Dto(CrmReply r) => new
    {
        id = r.Id, conversationId = r.ConversationId, contact = r.Contact, phone = r.Phone, incoming = r.Incoming, transcript = r.Transcript,
        reply = r.Reply, draft = r.Draft, kind = r.Kind, waitingSince = r.WaitingSince, attendant = r.Attendant, decision = r.Decision, reason = r.Reason, status = r.Status, error = r.Error, reviewedBy = r.ReviewedByName,
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
                // As aprovadas ficam um ano: são os exemplos com que a IA aprende.
                await db.CrmReplies.Where(r => r.UpdatedAt < old && r.Status != CrmReplyStatus.Sent).ExecuteDeleteAsync(ct);
                var year = time.GetUtcNow().UtcDateTime.AddDays(-365);
                await db.CrmReplies.Where(r => r.UpdatedAt < year).ExecuteDeleteAsync(ct);
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
