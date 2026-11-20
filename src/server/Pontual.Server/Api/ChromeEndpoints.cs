using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Sites abertos nos navegadores (Chrome, Edge, Opera, Firefox) que não são operacionais. O Terminal só avisa (a
/// atendente não vê nada); o TI decide aqui se libera (o aviso para) ou bloqueia (os navegadores passam a recusar o site).
/// A decisão vale para os subdomínios e pode ser só para uma atendente (<see cref="ChromeDeviceSite"/>) ou para todas
/// (<see cref="ChromeSite"/>). A de uma atendente vale mais: o YouTube pode estar bloqueado para todas e liberado para uma.
/// </summary>
public static class ChromeEndpoints
{
    /// <summary>Login do Google: entrar ou trocar de perfil no Chrome passa por estes domínios, que nunca geram aviso.</summary>
    public static readonly IReadOnlyList<string> AlwaysAllowed = ["accounts.google.com", "myaccount.google.com", "accounts.youtube.com"];

    /// <summary>Decisão que vale para todas as atendentes.</summary>
    public const string ScopeAll = "all";

    /// <summary>Decisão (ou site esperando decisão) de uma atendente só.</summary>
    public const string ScopeDevice = "device";

    /// <summary>Limite de domínios decididos: a lista vai inteira para cada Terminal e para a política dos navegadores.</summary>
    private const int MaxDecided = 1000;

    private sealed record DecideBody(string? Host, string? Decision, Guid? DeviceId);

    private sealed record SiteRow(
        string Host, string Status, string Scope, Guid? DeviceId, string? DeviceLabel, string? Browser,
        int Visits, int Minutes, DateTime? FirstAt, DateTime? LastAt, string? DecidedBy, DateTime? DecidedAt, DateTime SortAt,
        bool BlockedForAll = false);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/chrome-sites", ListAsync).Panel(Permission.AppsRead);
        api.MapPost("/chrome-sites/decide", DecideAsync).Panel(Permission.AppsWebsitesManage);
    }

    /// <summary>
    /// Texto do alerta de um domínio, igual para todos os navegadores: o mesmo texto não abre um segundo alerta e é
    /// resolvido quando o TI decide. O nome do navegador fica no histórico e na notificação.
    /// </summary>
    public static string AlertText(string host) => $"Abriu {host} num navegador, que não é um site operacional";

    /// <summary>Texto dos alertas abertos antes dos outros navegadores (só Chrome); também são resolvidos na decisão.</summary>
    private static string LegacyAlertText(string host) => $"Abriu {host} no Google Chrome, que não é um site operacional";

    /// <summary>
    /// O que vai na configuração de um computador: liberados (sem aviso) e bloqueados (recusados pelos navegadores).
    /// A decisão da atendente vence a decisão para todas no mesmo domínio e nos subdomínios dele.
    /// </summary>
    public static async Task<(List<string> Allowed, List<string> Blocked)> DecidedAsync(PontualDb db, Guid deviceId, CancellationToken ct)
    {
        var all = await db.ChromeSites.AsNoTracking()
            .Where(c => c.Status != ChromeSite.StatusPending)
            .Select(c => new { c.Host, c.Status })
            .ToListAsync(ct);
        var mine = await db.ChromeDeviceSites.AsNoTracking()
            .Where(c => c.DeviceId == deviceId && c.Status != ChromeSite.StatusPending)
            .Select(c => new { c.Host, c.Status })
            .ToListAsync(ct);

        var myAllowed = mine.Where(m => m.Status == ChromeSite.StatusAllowed).Select(m => m.Host).ToList();
        var myBlocked = mine.Where(m => m.Status == ChromeSite.StatusBlocked).Select(m => m.Host).ToList();
        var allowed = myAllowed
            .Concat(all.Where(a => a.Status == ChromeSite.StatusAllowed && !Input.HostMatches(a.Host, myBlocked)).Select(a => a.Host))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var blocked = myBlocked
            .Concat(all.Where(a => a.Status == ChromeSite.StatusBlocked && !Input.HostMatches(a.Host, myAllowed)).Select(a => a.Host))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return ([.. AlwaysAllowed, .. allowed], [.. blocked]);
    }

    private static async Task<IResult> ListAsync(PontualDb db, CancellationToken ct)
    {
        var labels = (await db.Devices.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Label);
        string? Label(Guid id) => id == Guid.Empty ? null : labels.GetValueOrDefault(id, "Máquina removida");

        var globals = await db.ChromeSites.AsNoTracking().ToListAsync(ct);
        var blockedForAll = globals.Where(s => s.Status == ChromeSite.StatusBlocked).Select(s => s.Host).ToList();
        var all = globals.Select(s => new SiteRow(
            s.Host, s.Status, ScopeAll, null, Label(s.DeviceId), null, s.Visits, s.Minutes,
            s.Visits > 0 ? s.FirstAt : null, s.Visits > 0 ? s.LastAt : null, s.DecidedBy, s.DecidedAt,
            s.Status == ChromeSite.StatusPending ? s.LastAt : s.DecidedAt ?? s.LastAt));
        // Pedido de um site bloqueado para todas: continua bloqueado para ela enquanto o TI não decide.
        var mine = (await db.ChromeDeviceSites.AsNoTracking().ToListAsync(ct)).Select(s => new SiteRow(
            s.Host, s.Status, ScopeDevice, s.DeviceId, Label(s.DeviceId), s.Browser.Length > 0 ? s.Browser : null, s.Visits, s.Minutes,
            s.Visits > 0 ? s.FirstAt : null, s.Visits > 0 ? s.LastAt : null, s.DecidedBy, s.DecidedAt,
            s.Status == ChromeSite.StatusPending ? s.LastAt : s.DecidedAt ?? s.LastAt,
            s.Status == ChromeSite.StatusPending && Input.HostMatches(s.Host, blockedForAll)));

        // Os que esperam decisão primeiro; dentro de cada grupo, o mais recente no alto.
        return Results.Ok(all.Concat(mine)
            .OrderBy(s => s.Status == ChromeSite.StatusPending ? 0 : 1)
            .ThenByDescending(s => s.SortAt)
            .Take(2000)
            .Select(s => new
            {
                host = s.Host,
                status = s.Status,
                scope = s.Scope,
                deviceId = s.DeviceId,
                deviceLabel = s.DeviceLabel,
                browser = s.Browser,
                visits = s.Visits,
                minutes = s.Minutes,
                firstAt = s.FirstAt,
                lastAt = s.LastAt,
                decidedBy = s.DecidedBy,
                decidedAt = s.DecidedAt,
                blockedForAll = s.BlockedForAll,
            }));
    }

    private static async Task<IResult> DecideAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<DecideBody>();
        if (!Input.TryHost(body.Host, out var host) || !host.Contains('.'))
        {
            throw ApiError.BadRequest("Informe um domínio válido, como exemplo.com.br.");
        }

        // "reset" (só para uma atendente): apaga a decisão dela, e volta a valer a decisão para todas.
        var status = body.Decision switch
        {
            "allow" => ChromeSite.StatusAllowed,
            "block" => ChromeSite.StatusBlocked,
            "reset" when body.DeviceId is not null => null,
            _ => throw ApiError.BadRequest("Escolha liberar ou bloquear."),
        };

        if (status == ChromeSite.StatusBlocked && Input.HostMatches(host, AlwaysAllowed))
        {
            throw ApiError.BadRequest("Este domínio é do login do Google. Bloqueado, os perfis do Chrome deixam de funcionar.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var texts = new[] { AlertText(host), LegacyAlertText(host) };
        if (body.DeviceId is { } deviceId)
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                ?? throw ApiError.NotFound("Computador não encontrado.");
            var mine = await db.ChromeDeviceSites.FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.Host == host, ct);

            // Decidido o domínio para ela, os avisos dela sobre ele deixam de ser pendência.
            foreach (var alert in await db.Alerts.Where(a => a.DeviceId == deviceId && a.ResolvedAt == null && texts.Contains(a.Text)).ToListAsync(ct))
            {
                alert.ResolvedAt = now;
            }

            if (status is null)
            {
                if (mine is not null)
                {
                    db.ChromeDeviceSites.Remove(mine);
                    await audit.SaveAsync(db, AuditInfo.By(admin, "chrome.reset",
                        $"{admin.Name} tirou a decisão do site {host} só para {device.Label}; vale a decisão para todas", "chrome", mine.Id), bumpConfig: true, ct);
                }
                else
                {
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new { host, status = "reset", deviceId });
            }

            if (mine is null)
            {
                await EnsureRoomAsync(db, ct);
                mine = new ChromeDeviceSite { Id = Guid.NewGuid(), DeviceId = deviceId, Host = host, FirstAt = now, LastAt = now };
                db.ChromeDeviceSites.Add(mine);
            }

            var changedMine = mine.Status != status;
            mine.Status = status;
            mine.DecidedBy = admin.Name;
            mine.DecidedAt = now;
            if (changedMine)
            {
                var allow = status == ChromeSite.StatusAllowed;
                await audit.SaveAsync(db, AuditInfo.By(admin, allow ? "chrome.allow" : "chrome.block",
                    $"{admin.Name} {(allow ? "liberou" : "bloqueou")} o site {host} só para {device.Label}", "chrome", mine.Id), bumpConfig: true, ct);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { host, status, deviceId });
        }

        var site = await db.ChromeSites.FirstOrDefaultAsync(c => c.Host == host, ct);
        if (site is null)
        {
            await EnsureRoomAsync(db, ct);
            site = new ChromeSite { Id = Guid.NewGuid(), Host = host, FirstAt = now, LastAt = now };
            db.ChromeSites.Add(site);
        }

        var changed = site.Status != status;
        site.Status = status!;
        site.DecidedBy = admin.Name;
        site.DecidedAt = now;

        // Decidido para todas: os avisos abertos e os pedidos de cada atendente sobre o domínio deixam de ser pendência.
        foreach (var alert in await db.Alerts.Where(a => a.ResolvedAt == null && texts.Contains(a.Text)).ToListAsync(ct))
        {
            alert.ResolvedAt = now;
        }

        db.ChromeDeviceSites.RemoveRange(await db.ChromeDeviceSites.Where(c => c.Host == host && c.Status == ChromeSite.StatusPending).ToListAsync(ct));

        if (changed)
        {
            var allow = status == ChromeSite.StatusAllowed;
            await audit.SaveAsync(db, AuditInfo.By(admin, allow ? "chrome.allow" : "chrome.block",
                $"{admin.Name} {(allow ? "liberou" : "bloqueou")} o site {host} para todas as atendentes", "chrome", site.Id), bumpConfig: true, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { host, status, deviceId = (Guid?)null });
    }

    private static async Task EnsureRoomAsync(PontualDb db, CancellationToken ct)
    {
        var decided = await db.ChromeSites.CountAsync(c => c.Status != ChromeSite.StatusPending, ct)
            + await db.ChromeDeviceSites.CountAsync(c => c.Status != ChromeSite.StatusPending, ct);
        if (decided >= MaxDecided)
        {
            throw ApiError.BadRequest($"A lista dos navegadores aceita no máximo {MaxDecided} domínios decididos.");
        }
    }
}
