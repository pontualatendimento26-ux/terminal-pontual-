using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Cobertura de ausência (ver <see cref="Coverage"/>). O Terminal da atendente ausente lê as conversas não
/// lidas do WhatsApp e pergunta aqui o que responder; o que vai sozinho volta para ele enviar, e o que precisa
/// de aprovação aparece na Central Neural, na ficha da atendente. A supervisão liga, desliga ou põe em ensaio.
/// </summary>
public static class CoverageEndpoints
{
    private sealed record ReadBody(string? Contact, string? Text);
    private sealed record ResultBody(bool? Sent, bool? Holding, string? Error);
    private sealed record ModeBody(string? Mode);
    private sealed record AbsenceBody(bool? Absent);
    private sealed record ApproveBody(string? Message);
    private sealed record DayBody(int? Day, bool? On, string? From, string? To);
    private sealed record ScheduleBody(List<DayBody>? Days);

    private static readonly ConcurrentDictionary<Guid, byte> Busy = new();

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/device/assistant/coverage", ReplyAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/assistant/coverage/{id:guid}/result", ResultAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapGet("/device/assistant/coverage/outbox", OutboxAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);

        api.MapPut("/neural/attendants/{deviceId:guid}/coverage", SetModeAsync).Panel(Permission.ConfigEdit);
        api.MapPut("/neural/attendants/{deviceId:guid}/coverage/schedule", SetScheduleAsync).Panel(Permission.ConfigEdit);
        api.MapPut("/neural/attendants/{deviceId:guid}/absence", SetAbsenceAsync).Panel(Permission.ConfigEdit);
        api.MapPost("/neural/coverage/{id:guid}/approve", ApproveAsync).Panel(Permission.ConfigEdit);
        api.MapPost("/neural/coverage/{id:guid}/reject", RejectAsync).Panel(Permission.ConfigEdit);
    }

    /// <summary>
    /// Modo que vale agora para a máquina. A cobertura só existe no dia em que a supervisão marcou que a atendente
    /// faltou: aí é "ligada" (ou "ensaio", se foi escolhido), dentro do horário dela e com a assistente ligada.
    /// Se a atendente veio trabalhar e não está respondendo o WhatsApp, a IA não entra. É o que o heartbeat entrega.
    /// </summary>
    public static async Task<string> ActiveModeAsync(PontualDb db, IConfiguration configuration, Device device, DateTime utcNow, CancellationToken ct)
    {
        var profile = await db.AssistantProfiles.AsNoTracking().Where(p => p.DeviceId == device.Id)
            .Select(p => new { p.Coverage, p.CoverageSchedule, p.AbsentOn }).FirstOrDefaultAsync(ct);
        // Só no dia marcado como falta; o modo escolhido decide apenas entre enviar e só escrever (ensaio).
        var mode = profile is null || profile.AbsentOn != Coverage.Day(utcNow) ? CoverageMode.Off
            : profile.Coverage == CoverageMode.Test ? CoverageMode.Test : CoverageMode.On;
        return mode.Length > 0 && Coverage.WithinHours(utcNow, profile?.CoverageSchedule)
            && await AssistantEndpoints.IsEnabledAsync(db, configuration, device, ct)
            ? mode
            : CoverageMode.Off;
    }

    /// <summary>Se a supervisão marcou que a atendente desta máquina faltou hoje.</summary>
    public static async Task<bool> AbsentTodayAsync(PontualDb db, Guid deviceId, DateTime utcNow, CancellationToken ct)
    {
        var today = Coverage.Day(utcNow);
        return await db.AssistantProfiles.AsNoTracking().AnyAsync(p => p.DeviceId == deviceId && p.AbsentOn == today, ct);
    }

    /// <summary>A agenda para a tela: sete dias, de domingo (0) a sábado (6). Dia desmarcado volta com 08:00 e 18:00 só para preencher os campos.</summary>
    public static object[] ScheduleDto(string? schedule) =>
        [.. Coverage.ParseSchedule(schedule).Select((hours, day) => (object)new
        {
            day,
            on = hours is not null,
            from = Coverage.Clock(hours?.From ?? 8 * 60),
            to = Coverage.Clock(hours?.To ?? 18 * 60),
        })];

    // ------------------------------------------------------------------ terminal

    private static object Skip(string reason) => new { action = "skip", reason };

    private static async Task<IResult> ReplyAsync(HttpContext context, PontualDb db, IConfiguration configuration, IAssistantModel model, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var now = time.GetUtcNow().UtcDateTime;
        var body = await context.ReadBodyAsync<ReadBody>();
        var contact = Input.Truncated(body.Contact, 80);
        var transcript = AssistantEndpoints.Multiline(body.Text);
        if (transcript.Length > Coverage.MaxTranscript)
        {
            transcript = transcript[^Coverage.MaxTranscript..];
        }

        var mode = await ActiveModeAsync(db, configuration, device, now, ct);
        if (mode == CoverageMode.Off)
        {
            return Results.Ok(Skip("off"));
        }

        var incoming = Coverage.Incoming(transcript);
        if (contact.Length == 0 || incoming.Length == 0)
        {
            return Results.Ok(Skip("answered"));
        }

        var fingerprint = Coverage.Fingerprint(contact, incoming);
        if (await db.CoverageReplies.AsNoTracking().AnyAsync(r => r.DeviceId == device.Id && r.Fingerprint == fingerprint, ct))
        {
            return Results.Ok(Skip("done"));
        }

        // Duas mensagens seguidas ao mesmo cliente parecem robô: espera um pouco e responde tudo de uma vez.
        var gap = now - Coverage.ContactGap;
        if (mode == CoverageMode.On && await db.CoverageReplies.AsNoTracking()
                .AnyAsync(r => r.DeviceId == device.Id && r.Contact == contact && r.SentAt != null && r.SentAt > gap, ct))
        {
            return Results.Ok(new { action = "retry", reason = "gap" });
        }

        var apiKey = await AssistantSecrets.ReadKeyAsync(db, configuration, ct);
        if (apiKey is null || !Busy.TryAdd(device.Id, 0))
        {
            return Results.Ok(new { action = "retry", reason = "busy" });
        }

        try
        {
            var old = now - Coverage.Keep;
            await db.CoverageReplies.Where(r => r.CreatedAt < old).ExecuteDeleteAsync(ct);

            Coverage.Decision decision;
            try
            {
                var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
                var (cached, found) = await InstructionsAsync(db, device, settings, incoming, now, ct);
                var input = new StringBuilder()
                    .Append("<contato>").Append(contact).Append("</contato>\n\n")
                    .Append("<conversa_na_tela>\n").Append(transcript).Append("\n</conversa_na_tela>\n\n")
                    .Append("<mensagens_novas_do_cliente>\n").Append(incoming).Append("\n</mensagens_novas_do_cliente>");
                var json = await model.AskJsonAsync(
                    new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(settings), found, input.ToString(), Coverage.Schema, cached, new AssistantUsageTag(UsageKind.Coverage, device.Id)), ct);
                decision = Coverage.Parse(json);
            }
            catch (AssistantFailure)
            {
                // A IA não respondeu: nada é gravado e o Terminal tenta de novo na próxima volta.
                return Results.Ok(new { action = "retry", reason = "ai" });
            }

            var hour = now.AddHours(-1);
            var sentLastHour = await db.CoverageReplies.AsNoTracking().CountAsync(r => r.DeviceId == device.Id && r.SentAt != null && r.SentAt > hour, ct);
            var action = decision.Action;
            var reason = decision.Reason;
            if (action == Coverage.Send && sentLastHour >= Coverage.SendsPerHour)
            {
                action = Coverage.Queue;
                reason = $"Passou de {Coverage.SendsPerHour} mensagens enviadas sozinhas na última hora; esta espera aprovação. {reason}";
            }

            // Para o cliente, quem responde é a própria atendente. Um texto que cita a atendente em terceira pessoa,
            // ou fala em robô, IA ou "dando continuidade", não sai sozinho: a supervisão confere e corrige.
            var holding = decision.Holding;
            if (action == Coverage.Send && Coverage.RevealsCover(decision.Message, device.AttendantName))
            {
                action = Coverage.Queue;
                reason = $"A resposta citava a atendente ou dava a entender que não era ela respondendo; precisa ser corrigida antes de sair. {reason}";
            }

            if (Coverage.RevealsCover(holding, device.AttendantName))
            {
                holding = "";
            }

            var reply = new CoverageReply
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                Contact = contact,
                Incoming = incoming,
                Transcript = transcript,
                Reply = decision.Message,
                Holding = action == Coverage.Queue ? holding : "",
                Decision = action,
                Reason = Input.Truncated(reason, 400),
                Fingerprint = fingerprint,
                CreatedAt = now,
                UpdatedAt = now,
                Status = action == Coverage.None ? CoverageStatus.Skipped
                    : mode == CoverageMode.Test ? CoverageStatus.Draft
                    : action == Coverage.Send ? CoverageStatus.Sending
                    : CoverageStatus.Pending,
            };
            db.CoverageReplies.Add(reply);
            await db.SaveChangesAsync(ct);

            if (reply.Status == CoverageStatus.Sending)
            {
                return Results.Ok(new { action = "send", id = reply.Id, message = reply.Reply, holding = false });
            }

            if (reply.Status == CoverageStatus.Pending && reply.Holding.Length > 0)
            {
                return Results.Ok(new { action = "send", id = reply.Id, message = reply.Holding, holding = true });
            }

            return Results.Ok(Skip(reply.Status));
        }
        finally
        {
            Busy.TryRemove(device.Id, out _);
        }
    }

    /// <summary>
    /// As instruções da cobertura em duas partes. A parte em cache é a base (igual para todas as atendentes) e,
    /// depois, o perfil e as regras desta atendente (iguais o dia todo): cada mensagem de cliente lê do cache em vez
    /// de pagar a base inteira de novo. Com a base grande, os trechos achados para a mensagem mudam a cada pedido e
    /// por isso vão por último, fora do cache.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Cached, string Found)> InstructionsAsync(
        PontualDb db, Device device, Dictionary<string, string> settings, string incoming, DateTime now, CancellationToken ct)
    {
        var scripts = KnowledgeText.Ordered(await db.AssistantScripts.AsNoTracking().Where(s => s.Enabled && s.Content != "").ToListAsync(ct));
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

        var profile = await db.AssistantProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == device.Id, ct);
        var personal = AssistantPrompt.Personal(device, profile, now, await AssistantEndpoints.LearnedAsync(db, device, settings, ct));
        var name = string.IsNullOrWhiteSpace(device.AttendantName) ? "desta máquina" : device.AttendantName;
        return ([shared, personal + "\n\n" + Coverage.Rules(name)], found);
    }

    /// <summary>O Terminal conta se a mensagem saiu. O aviso imediato não muda a situação: a resposta continua esperando aprovação.</summary>
    private static async Task<IResult> ResultAsync(Guid id, HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var body = await context.ReadBodyAsync<ResultBody>();
        var reply = await db.CoverageReplies.FirstOrDefaultAsync(r => r.Id == id && r.DeviceId == device.Id, ct) ?? throw ApiError.NotFound("Resposta não encontrada.");
        var now = time.GetUtcNow().UtcDateTime;
        var sent = body.Sent == true;
        var error = Input.Truncated(body.Error, 300);

        if (body.Holding == true)
        {
            reply.Error = sent ? "" : "O aviso imediato não saiu: " + (error.Length > 0 ? error : "falha no envio.");
        }
        else if (reply.Status is CoverageStatus.Sending or CoverageStatus.Approved)
        {
            reply.Status = sent ? CoverageStatus.Sent : CoverageStatus.Failed;
            reply.SentAt = sent ? now : null;
            reply.Error = sent ? "" : (error.Length > 0 ? error : "Falha no envio.");
        }

        reply.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Respostas aprovadas na Central esperando o Terminal enviar.</summary>
    private static async Task<IResult> OutboxAsync(HttpContext context, PontualDb db, IConfiguration configuration, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        if (await ActiveModeAsync(db, configuration, device, time.GetUtcNow().UtcDateTime, ct) != CoverageMode.On)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var items = await db.CoverageReplies.AsNoTracking()
            .Where(r => r.DeviceId == device.Id && r.Status == CoverageStatus.Approved)
            .OrderBy(r => r.UpdatedAt).Take(5)
            .Select(r => new { id = r.Id, contact = r.Contact, message = r.Reply })
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    // ------------------------------------------------------------------ central

    private static readonly Dictionary<string, string> ModeLabel = new()
    {
        [CoverageMode.Off] = "desligou a cobertura de ausência",
        [CoverageMode.Test] = "pôs em ensaio a cobertura de ausência",
        [CoverageMode.On] = "ligou a cobertura de ausência",
    };

    private static async Task<IResult> SetModeAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ModeBody>();
        var mode = body.Mode ?? "";
        if (!CoverageMode.All.Contains(mode))
        {
            throw ApiError.BadRequest("Escolha desligada, ensaio ou ligada.");
        }

        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        var profile = await db.AssistantProfiles.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (profile is null)
        {
            profile = new AssistantProfile { DeviceId = deviceId };
            db.AssistantProfiles.Add(profile);
        }

        if (profile.Coverage != mode)
        {
            profile.Coverage = mode;
            profile.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await audit.SaveAsync(db, AuditInfo.By(admin, "coverage.mode", $"{admin.Name} {ModeLabel[mode]} de {device.Label}", "device", device.Id), cancellationToken: ct);
        }

        return Results.Ok(new { mode });
    }

    /// <summary>Marca (ou desmarca) que a atendente faltou hoje: a IA passa a responder os clientes dela na hora.</summary>
    private static async Task<IResult> SetAbsenceAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<AbsenceBody>();
        var now = time.GetUtcNow().UtcDateTime;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        var profile = await db.AssistantProfiles.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (profile is null)
        {
            profile = new AssistantProfile { DeviceId = deviceId };
            db.AssistantProfiles.Add(profile);
        }

        var absent = body.Absent == true;
        var value = absent ? Coverage.Day(now) : "";
        if (profile.AbsentOn != value)
        {
            profile.AbsentOn = value;
            profile.UpdatedAt = now;
            var text = absent
                ? $"{admin.Name} marcou que {device.Label} faltou hoje (a IA responde por ela)"
                : $"{admin.Name} desmarcou a falta de {device.Label}";
            await audit.SaveAsync(db, AuditInfo.By(admin, "coverage.absence", text, "device", device.Id), cancellationToken: ct);
        }

        return Results.Ok(new { absent });
    }

    /// <summary>Grava os dias e horários em que a IA pode atender por esta atendente. Não liga nem desliga a cobertura.</summary>
    private static async Task<IResult> SetScheduleAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ScheduleBody>();
        var days = new Coverage.DayHours?[Coverage.DayNames.Length];
        foreach (var day in body.Days ?? [])
        {
            if (day.Day is not { } index || index < 0 || index >= days.Length)
            {
                throw ApiError.BadRequest("Dia da semana inválido.");
            }

            if (day.On != true)
            {
                continue;
            }

            var name = Coverage.DayNames[index];
            if (!Coverage.TryClock(day.From, out var from) || !Coverage.TryClock(day.To, out var to))
            {
                throw ApiError.BadRequest($"Preencha o início e o fim do horário de {name}.");
            }

            if (from >= to)
            {
                throw ApiError.BadRequest($"Em {name}, o fim precisa ser depois do início.");
            }

            days[index] = new Coverage.DayHours(from, to);
        }

        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        var profile = await db.AssistantProfiles.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (profile is null)
        {
            profile = new AssistantProfile { DeviceId = deviceId };
            db.AssistantProfiles.Add(profile);
        }

        var schedule = Coverage.FormatSchedule(days);
        if (Coverage.FormatSchedule(Coverage.ParseSchedule(profile.CoverageSchedule)) != schedule)
        {
            profile.CoverageSchedule = schedule;
            profile.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await audit.SaveAsync(db, AuditInfo.By(admin, "coverage.schedule",
                $"{admin.Name} definiu quando a IA atende por {device.Label}: {Coverage.Describe(days)}", "device", device.Id), cancellationToken: ct);
        }

        return Results.Ok(new { schedule = ScheduleDto(schedule) });
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ApproveBody>();
        var reply = await db.CoverageReplies.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiError.NotFound("Resposta não encontrada.");
        if (reply.Status is not (CoverageStatus.Pending or CoverageStatus.Failed))
        {
            throw ApiError.Conflict("Esta resposta não está esperando aprovação.");
        }

        var message = AssistantEndpoints.Multiline(body.Message ?? reply.Reply);
        if (message.Length == 0)
        {
            throw ApiError.BadRequest("Escreva a mensagem que vai para o cliente.");
        }

        if (message.Length > Coverage.MaxReply)
        {
            throw ApiError.BadRequest($"A mensagem aceita no máximo {Coverage.MaxReply} caracteres.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        reply.Reply = message;
        reply.Status = CoverageStatus.Approved;
        reply.Error = "";
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == reply.DeviceId, ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "coverage.approve",
            $"{admin.Name} aprovou uma resposta da cobertura para um cliente de {device?.Label ?? "uma máquina removida"}", "device", reply.DeviceId), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    private static async Task<IResult> RejectAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var reply = await db.CoverageReplies.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiError.NotFound("Resposta não encontrada.");
        if (reply.Status is not (CoverageStatus.Pending or CoverageStatus.Failed or CoverageStatus.Approved))
        {
            throw ApiError.Conflict("Esta resposta não está esperando aprovação.");
        }

        reply.Status = CoverageStatus.Rejected;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await audit.SaveAsync(db, AuditInfo.By(admin, "coverage.reject",
            $"{admin.Name} recusou uma resposta da cobertura", "device", reply.DeviceId), cancellationToken: ct);
        return Results.Ok(Dto(reply));
    }

    /// <summary>As respostas recentes de uma máquina, para a ficha da atendente (a mais nova primeiro).</summary>
    public static async Task<object> ListAsync(PontualDb db, Guid deviceId, DateTime utcNow, CancellationToken ct)
    {
        var profile = await db.AssistantProfiles.AsNoTracking().Where(p => p.DeviceId == deviceId)
            .Select(p => new { p.Coverage, p.AbsentOn, p.CoverageSchedule }).FirstOrDefaultAsync(ct);
        var mode = profile?.Coverage ?? "";
        var absent = profile?.AbsentOn == Coverage.Day(utcNow);
        // Para a tela dizer na hora por que a IA não está respondendo mesmo com a falta marcada.
        var withinHours = Coverage.WithinHours(utcNow, profile?.CoverageSchedule);
        var hours = Coverage.Describe(Coverage.ParseSchedule(profile?.CoverageSchedule));
        var replies = await db.CoverageReplies.AsNoTracking()
            .Where(r => r.DeviceId == deviceId).OrderByDescending(r => r.CreatedAt).Take(60).ToListAsync(ct);
        var status = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId)
            .Select(d => new { d.CoverageStatus, d.CoverageStatusAt }).FirstOrDefaultAsync(ct);
        return new { mode, absent, withinHours, hours, replies = replies.Select(Dto), status = status?.CoverageStatus ?? "", statusAt = status?.CoverageStatusAt };
    }

    private static object Dto(CoverageReply r) => new
    {
        id = r.Id, contact = r.Contact, incoming = r.Incoming, transcript = r.Transcript, reply = r.Reply, holding = r.Holding,
        decision = r.Decision, reason = r.Reason, status = r.Status, error = r.Error, reviewedBy = r.ReviewedByName,
        createdAt = r.CreatedAt, sentAt = r.SentAt,
    };
}
