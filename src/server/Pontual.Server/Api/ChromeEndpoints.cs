using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Sites abertos no Google Chrome que não são operacionais. O Terminal só avisa (a atendente não vê nada);
/// o TI decide aqui se libera (o aviso para) ou bloqueia (o Chrome daquela conta passa a recusar o site).
/// A decisão vale para todos os computadores e para os subdomínios do domínio escolhido.
/// </summary>
public static class ChromeEndpoints
{
    /// <summary>Login do Google: entrar ou trocar de perfil no Chrome passa por estes domínios, que nunca geram aviso.</summary>
    public static readonly IReadOnlyList<string> AlwaysAllowed = ["accounts.google.com", "myaccount.google.com", "accounts.youtube.com"];

    /// <summary>Limite de domínios decididos: a lista vai inteira para cada Terminal e para a política do Chrome.</summary>
    private const int MaxDecided = 1000;

    private sealed record DecideBody(string? Host, string? Decision);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/chrome-sites", ListAsync).Panel(Permission.AppsRead);
        api.MapPost("/chrome-sites/decide", DecideAsync).Panel(Permission.AppsWebsitesManage);
    }

    /// <summary>Texto do alerta de um domínio: o mesmo texto não abre um segundo alerta e é resolvido quando o TI decide.</summary>
    public static string AlertText(string host) => $"Abriu {host} no Google Chrome, que não é um site operacional";

    /// <summary>O que vai na configuração de cada Terminal: liberados (sem aviso) e bloqueados (recusados pelo Chrome).</summary>
    public static async Task<(List<string> Allowed, List<string> Blocked)> DecidedAsync(PontualDb db, CancellationToken ct)
    {
        var decided = await db.ChromeSites.AsNoTracking()
            .Where(c => c.Status != ChromeSite.StatusPending)
            .Select(c => new { c.Host, c.Status })
            .ToListAsync(ct);
        return (
            [.. AlwaysAllowed, .. decided.Where(d => d.Status == ChromeSite.StatusAllowed).Select(d => d.Host).Order(StringComparer.Ordinal)],
            [.. decided.Where(d => d.Status == ChromeSite.StatusBlocked).Select(d => d.Host).Order(StringComparer.Ordinal)]);
    }

    private static async Task<IResult> ListAsync(PontualDb db, CancellationToken ct)
    {
        var labels = (await db.Devices.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Label);
        var sites = await db.ChromeSites.AsNoTracking().ToListAsync(ct);

        // Os que esperam decisão primeiro; dentro de cada grupo, o mais recente no alto.
        return Results.Ok(sites
            .OrderBy(s => s.Status == ChromeSite.StatusPending ? 0 : 1)
            .ThenByDescending(s => s.Status == ChromeSite.StatusPending ? s.LastAt : s.DecidedAt ?? s.LastAt)
            .Take(2000)
            .Select(s => new
            {
                host = s.Host,
                status = s.Status,
                deviceLabel = s.DeviceId == Guid.Empty ? null : labels.GetValueOrDefault(s.DeviceId, "Máquina removida"),
                visits = s.Visits,
                minutes = s.Minutes,
                firstAt = s.Visits > 0 ? s.FirstAt : (DateTime?)null,
                lastAt = s.Visits > 0 ? s.LastAt : (DateTime?)null,
                decidedBy = s.DecidedBy,
                decidedAt = s.DecidedAt,
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

        var allow = body.Decision switch
        {
            "allow" => true,
            "block" => false,
            _ => throw ApiError.BadRequest("Escolha liberar ou bloquear."),
        };

        if (!allow && Input.HostMatches(host, AlwaysAllowed))
        {
            throw ApiError.BadRequest("Este domínio é do login do Google. Bloqueado, os perfis do Chrome deixam de funcionar.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var site = await db.ChromeSites.FirstOrDefaultAsync(c => c.Host == host, ct);
        if (site is null)
        {
            if (await db.ChromeSites.CountAsync(c => c.Status != ChromeSite.StatusPending, ct) >= MaxDecided)
            {
                throw ApiError.BadRequest($"A lista do Chrome aceita no máximo {MaxDecided} domínios decididos.");
            }

            site = new ChromeSite { Id = Guid.NewGuid(), Host = host, FirstAt = now, LastAt = now };
            db.ChromeSites.Add(site);
        }

        var status = allow ? ChromeSite.StatusAllowed : ChromeSite.StatusBlocked;
        var changed = site.Status != status;
        site.Status = status;
        site.DecidedBy = admin.Name;
        site.DecidedAt = now;

        // Decidido o domínio, os avisos abertos sobre ele deixam de ser pendência.
        var text = AlertText(host);
        foreach (var alert in await db.Alerts.Where(a => a.ResolvedAt == null && a.Text == text).ToListAsync(ct))
        {
            alert.ResolvedAt = now;
        }

        if (changed)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, allow ? "chrome.allow" : "chrome.block",
                allow ? $"{admin.Name} liberou o site {host} no Google Chrome" : $"{admin.Name} bloqueou o site {host} no Google Chrome",
                "chrome", site.Id), bumpConfig: true, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { host, status });
    }
}
