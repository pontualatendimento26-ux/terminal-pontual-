using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Celular compartilhado (decisão do dono, 23/11/2026, "solução 2"): o mesmo celular passa por várias atendentes, então
/// quem pega o aparelho toca no próprio nome (sem senha) e, ao devolver, em "Sair". O nome vale para tudo o que o
/// aparelho registra dali em diante (uso, bloqueios, chamados, gravação). Só celulares trocam de atendente por aqui;
/// nos computadores quem define a atendente continua sendo o TI na Central.
/// </summary>
public static class SharedPhoneEndpoints
{
    private sealed record AttendantBody(string? Name);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/device/attendants", NamesAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/attendant", SetAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
    }

    internal static bool IsPhone(Device device) => (device.Windows ?? "").StartsWith("Android", StringComparison.OrdinalIgnoreCase);

    /// <summary>Os nomes das atendentes já conhecidas na Central (dos computadores e dos celulares), para escolher.</summary>
    private static async Task<IResult> NamesAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved || !IsPhone(device))
        {
            return Results.Ok(new { names = Array.Empty<string>() });
        }

        var names = await db.Devices.AsNoTracking()
            .Where(d => d.Approved && d.AttendantName != null && d.AttendantName != "")
            .Select(d => d.AttendantName!).Distinct().ToListAsync(ct);
        var known = await db.History.AsNoTracking()
            .Where(h => h.DeviceId == device.Id && h.Text.StartsWith(PickPrefix))
            .OrderByDescending(h => h.At).Take(200).Select(h => h.Text).ToListAsync(ct);
        names.AddRange(known.Select(t => t[PickPrefix.Length..].TrimEnd('.')));
        return Results.Ok(new
        {
            names = names.Select(n => n.Trim()).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(),
        });
    }

    private const string PickPrefix = "Celular pego por ";

    private static async Task<IResult> SetAsync(HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved)
        {
            throw new ApiError(StatusCodes.Status403Forbidden, "Aparelho ainda não aprovado.");
        }

        if (!IsPhone(device))
        {
            throw new ApiError(StatusCodes.Status403Forbidden, "Só celulares trocam de atendente pelo próprio aparelho.");
        }

        var body = await context.ReadBodyAsync<AttendantBody>();
        var name = Input.Truncated(body.Name, 80).Trim();
        var previous = device.AttendantName ?? "";
        if (name == previous)
        {
            return Results.Ok(new { attendantName = device.AttendantName });
        }

        var at = time.GetUtcNow().UtcDateTime;
        if (previous.Length > 0)
        {
            db.History.Add(new HistoryEntry { DeviceId = device.Id, At = at, Text = $"Celular devolvido por {previous}." });
        }

        if (name.Length > 0)
        {
            db.History.Add(new HistoryEntry { DeviceId = device.Id, At = at, Text = PickPrefix + name + "." });
        }

        device.AttendantName = name.Length > 0 ? name : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { attendantName = device.AttendantName });
    }
}
