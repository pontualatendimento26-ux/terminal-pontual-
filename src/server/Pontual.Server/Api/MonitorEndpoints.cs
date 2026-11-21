using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>Alertas, notificações, auditoria e domínios observados (contrato, seção 4).</summary>
public static class MonitorEndpoints
{
    private sealed record AllowBody(string? Host, Guid? ItemId);
    private sealed record ReadBody(long? UpTo);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/alerts", AlertsAsync).Panel(Permission.AlertsRead);
        api.MapPost("/alerts/{id:guid}/resolve", ResolveAsync).Panel(Permission.AlertsHandle);
        api.MapGet("/notifications", NotificationsAsync).Panel(Permission.DevicesRead);
        api.MapPost("/notifications/read", MarkReadAsync).Panel(Permission.DevicesRead);
        api.MapGet("/audit", AuditAsync).Panel(Permission.AuditRead);
        api.MapGet("/audit/verify", VerifyAsync).Panel(Permission.AuditRead);
        api.MapGet("/observed-hosts", ObservedAsync).Panel(Permission.AppsRead);
        api.MapPost("/observed-hosts/allow", AllowAsync).Panel(Permission.AppsWebsitesManage);
    }

    private static async Task<Dictionary<Guid, string>> DeviceLabelsAsync(PontualDb db, CancellationToken ct) =>
        (await db.Devices.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Label);

    private static async Task<IResult> AlertsAsync(PontualDb db, CancellationToken ct)
    {
        var labels = await DeviceLabelsAsync(db, ct);
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.ResolvedAt == null)
            .OrderByDescending(a => a.At)
            .Take(500)
            .ToListAsync(ct);

        return Results.Ok(alerts.Select(a => new
        {
            id = a.Id,
            deviceId = a.DeviceId,
            deviceLabel = labels.GetValueOrDefault(a.DeviceId, "Máquina removida"),
            text = a.Text,
            at = a.At,
        }));
    }

    private static async Task<IResult> ResolveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiError.NotFound("Alerta não encontrado.");
        if (alert.ResolvedAt is null)
        {
            alert.ResolvedAt = time.GetUtcNow().UtcDateTime;
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == alert.DeviceId, ct);
            await audit.SaveAsync(db, AuditInfo.By(admin, "alert.resolve",
                $"{admin.Name} resolveu o alerta de {device?.Label ?? "máquina removida"}: {alert.Text}", "alert", alert.Id), cancellationToken: ct);
        }

        return Results.NoContent();
    }

    // "Lida" é de cada pessoa: guarda, por conta, o número da última notificação que ela viu.
    // Assim, quando um do TI abre a lista, a bolinha "Nova" continua para os outros.
    private static string ReadKey(Guid adminId) => $"notifRead:{adminId:N}";

    private static async Task<IResult> NotificationsAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var key = ReadKey(context.Admin().Id);
        var lastRead = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => (long?)s.Value).FirstOrDefaultAsync(ct) ?? 0;

        var notifications = await db.Notifications.AsNoTracking()
            .OrderByDescending(n => n.Id)
            .Take(100)
            .Select(n => new { id = n.Id, at = n.At, text = n.Text, urgent = n.Urgent, read = n.Id <= lastRead })
            .ToListAsync(ct);

        return Results.Ok(notifications);
    }

    // Marcar como lida não é registrada na auditoria: não muda nada do que o sistema controla,
    // e o painel faz isso sozinho ao abrir a lista. Só vale para quem está logado.
    private static async Task<IResult> MarkReadAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var key = ReadKey(context.Admin().Id);
        // O corpo é opcional: sem ele, marca até a mais nova.
        var body = context.Request.ContentLength is > 0 ? await context.ReadBodyAsync<ReadBody>() : new ReadBody(null);
        var newest = await db.Notifications.MaxAsync(n => (long?)n.Id, ct) ?? 0;
        if (body.UpTo is { } upTo && upTo < newest)
        {
            newest = upTo;
        }
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = newest });
        }
        else if (setting.Value < newest)
        {
            setting.Value = newest;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AuditAsync(PontualDb db, CancellationToken ct)
    {
        var records = await db.Audit.AsNoTracking()
            .OrderByDescending(a => a.Sequence)
            .Take(200)
            .ToListAsync(ct);

        return Results.Ok(records.Select(a => new { sequence = a.Sequence, at = a.OccurredAt, actor = a.ActorName, text = a.Text }));
    }

    private static async Task<IResult> VerifyAsync(PontualDb db, CancellationToken ct)
    {
        // Lê a cadeia em partes, sem carregar a tabela inteira na memória.
        var result = AuditChain.Verify(AuditPages(db, ct));

        return Results.Ok(new
        {
            intact = result.IsIntact,
            failedSequence = result.FailedSequence,
            failure = result.Failure switch
            {
                AuditChainFailure.HashMismatch => "hashMismatch",
                AuditChainFailure.PreviousHashMismatch => "previousHashMismatch",
                AuditChainFailure.SequenceGap => "sequenceGap",
                _ => null,
            },
        });
    }

    private static IEnumerable<AuditEntry> AuditPages(PontualDb db, CancellationToken ct)
    {
        const int page = 2000;
        long after = long.MinValue;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = db.Audit.AsNoTracking().Where(a => a.Sequence > after).OrderBy(a => a.Sequence).Take(page).ToList();
            foreach (var record in chunk)
            {
                yield return record.ToEntry();
            }

            if (chunk.Count < page)
            {
                yield break;
            }

            after = chunk[^1].Sequence;
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<IResult> ObservedAsync(PontualDb db, CancellationToken ct)
    {
        var labels = await DeviceLabelsAsync(db, ct);
        var itemNames = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var observed = await db.ObservedHosts.AsNoTracking().OrderByDescending(o => o.LastAt).Take(500).ToListAsync(ct);

        return Results.Ok(observed.Select(o =>
        {
            // O item pode ter sido renomeado ou excluído depois da observação.
            var itemId = o.ItemId is { } id && itemNames.ContainsKey(id) ? o.ItemId : null;
            return new
            {
                host = o.Host,
                itemId,
                itemName = itemId is { } known ? itemNames[known] : o.ItemName,
                deviceLabel = labels.GetValueOrDefault(o.DeviceId, "Máquina removida"),
                count = o.Count,
                lastAt = o.LastAt,
            };
        }));
    }

    private static async Task<IResult> AllowAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<AllowBody>();
        if (!Input.TryHost(body.Host, out var host))
        {
            throw ApiError.BadRequest("Informe um domínio válido.");
        }

        if (body.ItemId is not { } itemId)
        {
            throw ApiError.BadRequest("Informe o site que vai receber o domínio.");
        }

        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw ApiError.NotFound("Item não encontrado.");
        if (item.Kind != ItemKind.Site)
        {
            throw ApiError.BadRequest("Só sites têm domínios.");
        }

        var hosts = item.ExtraHosts;
        var added = !item.Hosts().Contains(host);
        if (added)
        {
            if (hosts.Count >= 50)
            {
                throw ApiError.BadRequest("Um site aceita no máximo 50 domínios extras.");
            }

            hosts.Add(host);
            item.ExtraHosts = hosts;
        }

        var rows = await db.ObservedHosts.Where(o => o.Host == host).ToListAsync(ct);
        db.ObservedHosts.RemoveRange(rows);

        if (added || rows.Count > 0)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "observed.allow",
                $"{admin.Name} liberou o domínio {host} para {item.Name}", "item", item.Id), bumpConfig: added, ct);
        }

        return Results.NoContent();
    }
}
