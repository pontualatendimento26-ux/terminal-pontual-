using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Aplicativos do computador (Windows, instalados e baixados) abertos pela lista "Aplicativos" do Terminal. Na primeira
/// vez que uma atendente abre um aplicativo que não é programa cadastrado, o Terminal deixa abrir e manda o pedido para
/// cá; o TI libera (abre sem pedido) ou bloqueia (o Terminal não abre mais e fecha o que estiver aberto). A decisão é por
/// nome do .exe e pode ser só para uma atendente (<see cref="DeviceApp"/>) ou para todas (<see cref="AppDecision"/>).
/// A de uma atendente vale mais.
/// </summary>
public static class AppEndpoints
{
    public const string ScopeAll = "all";
    public const string ScopeDevice = "device";

    /// <summary>Limite de aplicativos decididos: a lista vai inteira para cada Terminal.</summary>
    private const int MaxDecided = 1000;

    private sealed record DecideBody(string? Exe, string? Decision, Guid? DeviceId, string? Name);

    private sealed record AppRow(
        string Exe, string Name, string? Path, string Status, string Scope, Guid? DeviceId, string? DeviceLabel,
        int Opens, DateTime? FirstAt, DateTime? LastAt, string? DecidedBy, DateTime? DecidedAt, DateTime SortAt, bool BlockedForAll = false);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/device-apps", ListAsync).Panel(Permission.AppsRead);
        api.MapPost("/device-apps/decide", DecideAsync).Panel(Permission.AppsExecutablesApprove);
    }

    /// <summary>Texto do alerta de um aplicativo: igual em todos os computadores, resolvido quando o TI decide.</summary>
    public static string AlertText(string exe) => $"Abriu o aplicativo {exe}, que espera a decisão do TI";

    /// <summary>O que vai na configuração de um computador: liberados e bloqueados (nomes de .exe). A decisão da atendente vence.</summary>
    public static async Task<(List<string> Allowed, List<string> Blocked)> DecidedAsync(PontualDb db, Guid deviceId, CancellationToken ct)
    {
        var all = await db.AppDecisions.AsNoTracking()
            .Where(a => a.Status != AppDecision.StatusPending)
            .Select(a => new { a.Exe, a.Status })
            .ToListAsync(ct);
        var mine = await db.DeviceApps.AsNoTracking()
            .Where(a => a.DeviceId == deviceId && a.Status != AppDecision.StatusPending)
            .Select(a => new { a.Exe, a.Status })
            .ToListAsync(ct);

        var decidedByMe = mine.Select(m => m.Exe).ToHashSet(StringComparer.Ordinal);
        var allowed = mine.Where(m => m.Status == AppDecision.StatusAllowed).Select(m => m.Exe)
            .Concat(all.Where(a => a.Status == AppDecision.StatusAllowed && !decidedByMe.Contains(a.Exe)).Select(a => a.Exe))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var blocked = mine.Where(m => m.Status == AppDecision.StatusBlocked).Select(m => m.Exe)
            .Concat(all.Where(a => a.Status == AppDecision.StatusBlocked && !decidedByMe.Contains(a.Exe)).Select(a => a.Exe))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (allowed, blocked);
    }

    private static async Task<IResult> ListAsync(PontualDb db, CancellationToken ct)
    {
        var labels = (await db.Devices.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Label);
        string Label(Guid id) => labels.GetValueOrDefault(id, "Máquina removida");

        var globals = await db.AppDecisions.AsNoTracking().ToListAsync(ct);
        var blockedForAll = globals.Where(a => a.Status == AppDecision.StatusBlocked).Select(a => a.Exe).ToHashSet(StringComparer.Ordinal);
        var all = globals.Select(a => new AppRow(
            a.Exe, a.Name, null, a.Status, ScopeAll, null, null, 0, null, null, a.DecidedBy, a.DecidedAt, a.DecidedAt ?? a.LastAt));
        var mine = (await db.DeviceApps.AsNoTracking().ToListAsync(ct)).Select(a => new AppRow(
            a.Exe, a.Name, a.Path.Length > 0 ? a.Path : null, a.Status, ScopeDevice, a.DeviceId, Label(a.DeviceId), a.Opens,
            a.Opens > 0 ? a.FirstAt : null, a.Opens > 0 ? a.LastAt : null, a.DecidedBy, a.DecidedAt,
            a.Status == AppDecision.StatusPending ? a.LastAt : a.DecidedAt ?? a.LastAt,
            a.Status == AppDecision.StatusPending && blockedForAll.Contains(a.Exe)));

        // Os que esperam decisão primeiro; dentro de cada grupo, o mais recente no alto.
        return Results.Ok(all.Concat(mine)
            .OrderBy(a => a.Status == AppDecision.StatusPending ? 0 : 1)
            .ThenByDescending(a => a.SortAt)
            .Take(2000)
            .Select(a => new
            {
                exe = a.Exe,
                name = a.Name,
                path = a.Path,
                status = a.Status,
                scope = a.Scope,
                deviceId = a.DeviceId,
                deviceLabel = a.DeviceLabel,
                opens = a.Opens,
                firstAt = a.FirstAt,
                lastAt = a.LastAt,
                decidedBy = a.DecidedBy,
                decidedAt = a.DecidedAt,
                blockedForAll = a.BlockedForAll,
            }));
    }

    private static async Task<IResult> DecideAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<DecideBody>();
        if (!Input.TryExe(body.Exe, out var exe))
        {
            throw ApiError.BadRequest("Informe o nome do aplicativo terminado em .exe, como spotify.exe.");
        }

        // "reset" (só para uma atendente): apaga a decisão dela, e volta a valer a decisão para todas.
        var status = body.Decision switch
        {
            "allow" => AppDecision.StatusAllowed,
            "block" => AppDecision.StatusBlocked,
            "reset" when body.DeviceId is not null => null,
            _ => throw ApiError.BadRequest("Escolha liberar ou bloquear."),
        };

        if (status == AppDecision.StatusAllowed && Input.IsRefusedProgram(exe))
        {
            throw ApiError.BadRequest($"O programa {exe} nunca pode ser liberado.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var name = Input.Truncated(body.Name, 60);
        var alertText = AlertText(exe);
        if (body.DeviceId is { } deviceId)
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                ?? throw ApiError.NotFound("Computador não encontrado.");
            var mine = await db.DeviceApps.FirstOrDefaultAsync(a => a.DeviceId == deviceId && a.Exe == exe, ct);
            var label = mine is { Name.Length: > 0 } ? $"{mine.Name} ({exe})" : exe;

            // Decidido o aplicativo para ela, os avisos dela sobre ele deixam de ser pendência.
            foreach (var alert in await db.Alerts.Where(a => a.DeviceId == deviceId && a.ResolvedAt == null && a.Text == alertText).ToListAsync(ct))
            {
                alert.ResolvedAt = now;
            }

            if (status is null)
            {
                if (mine is not null)
                {
                    db.DeviceApps.Remove(mine);
                    await audit.SaveAsync(db, AuditInfo.By(admin, "app.reset",
                        $"{admin.Name} tirou a decisão do aplicativo {label} só para {device.Label}; vale a decisão para todas", "app", mine.Id), bumpConfig: true, ct);
                }
                else
                {
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new { exe, status = "reset", deviceId });
            }

            if (mine is null)
            {
                await EnsureRoomAsync(db, ct);
                mine = new DeviceApp { Id = Guid.NewGuid(), DeviceId = deviceId, Exe = exe, Name = name, FirstAt = now, LastAt = now };
                db.DeviceApps.Add(mine);
            }

            var changedMine = mine.Status != status;
            mine.Status = status;
            mine.DecidedBy = admin.Name;
            mine.DecidedAt = now;
            if (changedMine)
            {
                var allow = status == AppDecision.StatusAllowed;
                await audit.SaveAsync(db, AuditInfo.By(admin, allow ? "app.allow" : "app.block",
                    $"{admin.Name} {(allow ? "liberou" : "bloqueou")} o aplicativo {label} só para {device.Label}", "app", mine.Id), bumpConfig: true, ct);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { exe, status, deviceId });
        }

        var decision = await db.AppDecisions.FirstOrDefaultAsync(a => a.Exe == exe, ct);
        if (decision is null)
        {
            await EnsureRoomAsync(db, ct);
            decision = new AppDecision { Id = Guid.NewGuid(), Exe = exe, FirstAt = now, LastAt = now };
            db.AppDecisions.Add(decision);
        }

        // O nome que a atendente vê vem dos pedidos; o TI pode informar um ao decidir de antemão.
        var pending = await db.DeviceApps.Where(a => a.Exe == exe && a.Status == AppDecision.StatusPending).ToListAsync(ct);
        var knownName = pending.Select(p => p.Name).FirstOrDefault(n => n.Length > 0) ?? name;
        if (knownName.Length > 0)
        {
            decision.Name = knownName;
        }

        var changed = decision.Status != status;
        decision.Status = status!;
        decision.DecidedBy = admin.Name;
        decision.DecidedAt = now;
        decision.LastAt = now;

        // Decidido para todas: os avisos abertos e os pedidos de cada atendente sobre o aplicativo deixam de ser pendência.
        foreach (var alert in await db.Alerts.Where(a => a.ResolvedAt == null && a.Text == alertText).ToListAsync(ct))
        {
            alert.ResolvedAt = now;
        }

        db.DeviceApps.RemoveRange(pending);

        var what = decision.Name.Length > 0 ? $"{decision.Name} ({exe})" : exe;
        if (changed)
        {
            var allow = status == AppDecision.StatusAllowed;
            await audit.SaveAsync(db, AuditInfo.By(admin, allow ? "app.allow" : "app.block",
                $"{admin.Name} {(allow ? "liberou" : "bloqueou")} o aplicativo {what} para todas as atendentes", "app", decision.Id), bumpConfig: true, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { exe, status, deviceId = (Guid?)null });
    }

    private static async Task EnsureRoomAsync(PontualDb db, CancellationToken ct)
    {
        var decided = await db.AppDecisions.CountAsync(a => a.Status != AppDecision.StatusPending, ct)
            + await db.DeviceApps.CountAsync(a => a.Status != AppDecision.StatusPending, ct);
        if (decided >= MaxDecided)
        {
            throw ApiError.BadRequest($"A lista de aplicativos aceita no máximo {MaxDecided} decisões.");
        }
    }
}
