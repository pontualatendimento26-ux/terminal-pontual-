using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>Máquinas, vistas pelo painel (contrato, seção 4).</summary>
public static class DeviceEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);

    private sealed record ApproveBody(string? AttendantName);
    private sealed record PatchBody(string? AttendantName, bool? ObservationMode, bool? StoreApps = null);
    private sealed record MaintenanceBody(string? Reason, int? Minutes);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/devices", ListAsync).Panel(Permission.DevicesRead);
        api.MapPost("/enrollment-codes", CreateCodeAsync).Panel(Permission.EnrollmentCreate);
        api.MapPost("/devices/{id:guid}/approve", ApproveAsync).Panel(Permission.DevicesManage);
        api.MapPatch("/devices/{id:guid}", PatchAsync).Panel(Permission.DevicesManage);
        api.MapDelete("/devices/{id:guid}", DeleteAsync).Panel(Permission.DevicesRevoke);
        api.MapPost("/devices/{id:guid}/maintenance", StartMaintenanceAsync).Panel(Permission.MaintenanceManage);
        api.MapDelete("/devices/{id:guid}/maintenance", EndMaintenanceAsync).Panel(Permission.MaintenanceManage);
        api.MapGet("/devices/{id:guid}/history", HistoryAsync).Panel(Permission.UsageRead);
    }

    private static async Task<Device> FindAsync(PontualDb db, Guid id, CancellationToken ct) =>
        await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");

    private static async Task<IResult> ListAsync(PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Hostname).ToListAsync(ct);
        var open = await db.Alerts.Where(a => a.ResolvedAt == null)
            .GroupBy(a => a.DeviceId)
            .Select(g => new { DeviceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.DeviceId, g => g.Count, ct);

        return Results.Ok(devices.Select(d => DeviceDto.From(d, open.GetValueOrDefault(d.Id), now)));
    }

    private static async Task<IResult> CreateCodeAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var now = time.GetUtcNow().UtcDateTime;
        var code = Secrets.NewEnrollmentCode();
        var expiresAt = now + CodeLifetime;

        await db.EnrollmentCodes.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct);
        db.EnrollmentCodes.Add(new EnrollmentCode
        {
            Id = Guid.NewGuid(),
            CodeHash = Secrets.Sha256(code),
            ExpiresAt = expiresAt,
            CreatedById = admin.Id,
        });

        // O código não entra na auditoria: quem lê o registro não deve conseguir cadastrar uma máquina.
        await audit.SaveAsync(db, AuditInfo.By(admin, "enrollment.create",
            $"{admin.Name} gerou um código de cadastro de máquina, válido por 30 minutos"), cancellationToken: ct);

        return Results.Ok(new { code = Secrets.FormatEnrollmentCode(code), expiresAt });
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<ApproveBody>();
        var attendant = Input.Required(body.AttendantName, "o nome da atendente", 80);
        var device = await FindAsync(db, id, ct);
        if (device.Approved)
        {
            throw ApiError.Conflict("Esta máquina já foi aprovada.");
        }

        device.Approved = true;
        device.AttendantName = attendant;

        await audit.SaveAsync(db, AuditInfo.By(admin, "device.approve",
            $"{admin.Name} aprovou a máquina {device.Hostname} para {attendant}", "device", device.Id), bumpConfig: true, ct);

        return Results.Ok(await DeviceDto.LoadAsync(db, device, time.GetUtcNow().UtcDateTime, ct));
    }

    private static async Task<IResult> PatchAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<PatchBody>();
        var device = await FindAsync(db, id, ct);
        var changes = new List<string>();

        if (body.AttendantName is not null)
        {
            var attendant = Input.Required(body.AttendantName, "o nome da atendente", 80);
            if (attendant != device.AttendantName)
            {
                device.AttendantName = attendant;
                changes.Add($"trocou a atendente da máquina {device.Hostname} para {attendant}");
            }
        }

        if (body.ObservationMode is { } observation && observation != device.ObservationMode)
        {
            device.ObservationMode = observation;
            changes.Add($"{(observation ? "ligou" : "desligou")} o modo de observação de {device.Label}");
        }

        if (body.StoreApps is { } store && store != device.StoreApps)
        {
            device.StoreApps = store;
            changes.Add($"{(store ? "ligou" : "desligou")} os aplicativos da Microsoft Store em {device.Label}");
        }

        if (changes.Count > 0)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "device.update",
                $"{admin.Name} {string.Join(" e ", changes)}", "device", device.Id), bumpConfig: true, ct);
        }

        return Results.Ok(await DeviceDto.LoadAsync(db, device, time.GetUtcNow().UtcDateTime, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await FindAsync(db, id, ct);

        db.Alerts.RemoveRange(await db.Alerts.Where(a => a.DeviceId == id).ToListAsync(ct));
        db.History.RemoveRange(await db.History.Where(h => h.DeviceId == id).ToListAsync(ct));
        db.ObservedHosts.RemoveRange(await db.ObservedHosts.Where(o => o.DeviceId == id).ToListAsync(ct));
        db.AssistantMessages.RemoveRange(await db.AssistantMessages.Where(m => m.DeviceId == id).ToListAsync(ct));
        db.SavedTexts.RemoveRange(await db.SavedTexts.Where(s => s.DeviceId == id).ToListAsync(ct));
        db.StickyNotes.RemoveRange(await db.StickyNotes.Where(n => n.DeviceId == id).ToListAsync(ct));
        db.AssistantProfiles.RemoveRange(await db.AssistantProfiles.Where(p => p.DeviceId == id).ToListAsync(ct));
        db.WhatsAppCaptures.RemoveRange(await db.WhatsAppCaptures.Where(c => c.DeviceId == id).ToListAsync(ct));
        db.CoverageReplies.RemoveRange(await db.CoverageReplies.Where(r => r.DeviceId == id).ToListAsync(ct));
        db.ChromeDeviceSites.RemoveRange(await db.ChromeDeviceSites.Where(c => c.DeviceId == id).ToListAsync(ct));

        // Tira a máquina dos públicos; a lista pode ficar vazia (ninguém), nunca vira "todas" por acidente.
        foreach (var item in await db.Items.Where(i => i.AudienceJson != null).ToListAsync(ct))
        {
            if (item.Audience is { } audience && audience.Remove(id))
            {
                item.Audience = audience;
            }
        }

        db.Devices.Remove(device);

        await audit.SaveAsync(db, AuditInfo.By(admin, "device.revoke",
            $"{admin.Name} removeu a máquina {device.Label}", "device", device.Id), bumpConfig: true, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> StartMaintenanceAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<MaintenanceBody>();
        var reason = Input.Required(body.Reason, "o motivo da manutenção", 300);
        if (body.Minutes is not (>= 5 and <= 480))
        {
            throw ApiError.BadRequest("A manutenção deve durar entre 5 e 480 minutos.");
        }

        var device = await FindAsync(db, id, ct);
        if (!device.Approved)
        {
            throw ApiError.Conflict("A máquina ainda não foi aprovada.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        device.MaintenanceReason = reason;
        device.MaintenanceUntil = now.AddMinutes(body.Minutes.Value);

        await audit.SaveAsync(db, AuditInfo.By(admin, "maintenance.start",
            $"{admin.Name} pôs {device.Label} em manutenção por {body.Minutes} minutos: {reason}", "device", device.Id, reason), bumpConfig: true, ct);

        return Results.Ok(await DeviceDto.LoadAsync(db, device, now, ct));
    }

    private static async Task<IResult> EndMaintenanceAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await FindAsync(db, id, ct);
        var now = time.GetUtcNow().UtcDateTime;

        if (device.MaintenanceUntil is not null)
        {
            var wasActive = device.InMaintenance(now);
            device.MaintenanceReason = null;
            device.MaintenanceUntil = null;

            if (wasActive)
            {
                await audit.SaveAsync(db, AuditInfo.By(admin, "maintenance.end",
                    $"{admin.Name} encerrou a manutenção de {device.Label}", "device", device.Id), bumpConfig: true, ct);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }
        }

        return Results.Ok(await DeviceDto.LoadAsync(db, device, now, ct));
    }

    private static async Task<IResult> HistoryAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == id, ct))
        {
            throw ApiError.NotFound("Máquina não encontrada.");
        }

        var entries = await db.History.AsNoTracking()
            .Where(h => h.DeviceId == id)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .Take(100)
            .Select(h => new { at = h.At, text = h.Text })
            .ToListAsync(ct);

        return Results.Ok(entries);
    }
}
