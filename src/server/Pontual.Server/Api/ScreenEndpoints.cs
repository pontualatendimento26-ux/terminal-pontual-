using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Visualização ao vivo da tela de uma máquina. Regras da casa:
/// só existe enquanto um administrador está com a janela aberta (o pedido vence sozinho em segundos);
/// só a ÚLTIMA imagem fica guardada, e só na memória (nada vai para o banco nem para o disco);
/// todo início de visualização entra na auditoria; e o terminal mostra um aviso à atendente enquanto envia.
/// </summary>
public sealed class ScreenWatch(TimeProvider time)
{
    /// <summary>Quanto tempo o pedido vale sem ser renovado. O painel renova a cada imagem que busca.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public void Start(Guid deviceId, string viewer) => _entries[deviceId] = new Entry { Until = Now + Lease, Viewer = viewer };

    public bool Renew(Guid deviceId)
    {
        if (Active(deviceId) is not { } entry)
        {
            return false;
        }

        entry.Until = Now + Lease;
        return true;
    }

    public void Stop(Guid deviceId) => _entries.TryRemove(deviceId, out _);

    /// <summary>Nome de quem está vendo, ou nulo se ninguém está.</summary>
    public string? Viewer(Guid deviceId) => Active(deviceId)?.Viewer;

    public bool Put(Guid deviceId, byte[] frame)
    {
        if (Active(deviceId) is not { } entry)
        {
            return false;
        }

        entry.Frame = frame;
        return true;
    }

    public byte[]? Latest(Guid deviceId) => Active(deviceId)?.Frame;

    private Entry? Active(Guid deviceId)
    {
        if (!_entries.TryGetValue(deviceId, out var entry))
        {
            return null;
        }

        if (entry.Until < Now)
        {
            // Venceu: a imagem guardada é descartada junto.
            _entries.TryRemove(deviceId, out _);
            return null;
        }

        return entry;
    }

    private sealed class Entry
    {
        public DateTime Until;
        public string Viewer = "";
        public byte[]? Frame;
    }
}

public static class ScreenEndpoints
{
    private const int MaxFrameBytes = 2 * 1024 * 1024;

    public static void Map(RouteGroupBuilder api)
    {
        // Painel: ver a tela de uma pessoa é leitura de uso individual.
        api.MapPost("/devices/{id:guid}/screen", StartAsync).Panel(Permission.UsageReadIndividual);
        api.MapGet("/devices/{id:guid}/screen", Frame).Panel(Permission.UsageReadIndividual);
        api.MapDelete("/devices/{id:guid}/screen", Stop).Panel(Permission.UsageReadIndividual);

        // Terminal: pergunta se alguém quer ver e, enquanto quiserem, manda uma imagem por vez.
        api.MapGet("/device/screen", Wanted).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/screen", UploadAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
    }

    private static async Task<IResult> StartAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, ScreenWatch watch, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (!device.Approved)
        {
            throw ApiError.Conflict("A máquina ainda não foi aprovada.");
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "screen.view",
            $"{admin.Name} abriu a visualização ao vivo da tela de {device.Label}", "device", device.Id), cancellationToken: ct);

        watch.Start(device.Id, admin.Name);
        return Results.NoContent();
    }

    private static IResult Frame(Guid id, HttpContext context, ScreenWatch watch)
    {
        // Buscar a imagem renova o pedido. Se ele já venceu, o painel precisa pedir de novo (e isso é auditado de novo).
        if (!watch.Renew(id))
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        context.Response.Headers.CacheControl = "no-store";
        return watch.Latest(id) is { } frame ? Results.File(frame, "image/jpeg") : Results.NoContent();
    }

    private static IResult Stop(Guid id, ScreenWatch watch)
    {
        watch.Stop(id);
        return Results.NoContent();
    }

    private static IResult Wanted(HttpContext context, ScreenWatch watch)
    {
        var device = context.Device();
        var viewer = device.Approved ? watch.Viewer(device.Id) : null;
        return Results.Ok(new { wanted = viewer is not null, viewer });
    }

    private static async Task<IResult> UploadAsync(HttpContext context, ScreenWatch watch, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved || watch.Viewer(device.Id) is null)
        {
            // Ninguém pediu: a imagem nem é lida.
            return Results.Ok(new { wanted = false });
        }

        // Uma tela inteira não cabe no limite geral de 256 KB; só esta rota aceita mais.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxFrameBytes;
        }

        if (context.Request.ContentLength is > MaxFrameBytes)
        {
            throw ApiError.BadRequest("Imagem grande demais.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFrameBytes)
            {
                throw ApiError.BadRequest("Imagem grande demais.");
            }

            buffer.Write(chunk, 0, read);
        }

        var frame = buffer.ToArray();
        // Só JPEG: o painel mostra o que chega aqui, então nada além de imagem é aceito.
        if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8 || frame[2] != 0xFF)
        {
            throw ApiError.BadRequest("A imagem precisa ser JPEG.");
        }

        return Results.Ok(new { wanted = watch.Put(device.Id, frame) });
    }
}
