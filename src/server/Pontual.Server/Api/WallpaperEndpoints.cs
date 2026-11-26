using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Papel de parede da tela inicial do Terminal, por computador (pedido do dono, 26/11/2026, caso Bianca).
/// O TI envia a imagem no painel do computador; a tela inicial (que esta Central serve ao Terminal) a busca com o
/// token da máquina e a põe de fundo. Não precisa de versão nova do Terminal.
/// Os arquivos ficam na pasta <c>papeis-de-parede</c>, ao lado do banco de dados.
/// </summary>
public static class WallpaperEndpoints
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPut("/devices/{id:guid}/wallpaper", UploadAsync).Panel(Permission.DevicesManage);
        api.MapGet("/devices/{id:guid}/wallpaper", PanelGetAsync).Panel(Permission.DevicesRead);
        api.MapDelete("/devices/{id:guid}/wallpaper", RemoveAsync).Panel(Permission.DevicesManage);
        api.MapGet("/device/wallpaper", TerminalGetAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
    }

    /// <summary>Recebe a imagem no corpo (nome em <c>X-File-Name</c>, codificado como em uma URL) e troca a anterior.</summary>
    private static async Task<IResult> UploadAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBytes + 64 * 1024;
        }

        string name;
        try
        {
            name = Path.GetFileName(Input.Truncated(Uri.UnescapeDataString(context.Request.Headers["X-File-Name"].ToString()), 150));
        }
        catch (UriFormatException)
        {
            name = "";
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length == 0 || !Types.ContainsKey(extension))
        {
            throw ApiError.BadRequest("Formato não aceito. Envie uma imagem PNG, JPG ou WEBP.");
        }

        if (context.Request.ContentLength > MaxBytes)
        {
            throw ApiError.BadRequest("A imagem passa de 8 MB. Escolha uma menor.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw ApiError.BadRequest("A imagem passa de 8 MB. Escolha uma menor.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (!LooksLikeImage(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), extension))
        {
            throw ApiError.BadRequest("O arquivo não é uma imagem PNG, JPG ou WEBP válida.");
        }

        var folder = Folder(db);
        Directory.CreateDirectory(folder);
        var fileName = $"{device.Id:N}-{time.GetUtcNow().ToUnixTimeMilliseconds()}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(folder, fileName), buffer.ToArray(), ct);

        var previous = device.Wallpaper;
        device.Wallpaper = fileName;
        // bumpConfig: o Terminal vê a versão nova no próximo sinal e a tela inicial busca a configuração (e a imagem) de novo.
        await audit.SaveAsync(db, AuditInfo.By(admin, "device.update",
            $"{admin.Name} trocou o papel de parede de {device.Label}", "device", device.Id), bumpConfig: true, ct);
        DeleteFile(folder, previous);

        return Results.Ok(await DeviceDto.LoadAsync(db, device, time.GetUtcNow().UtcDateTime, ct));
    }

    private static async Task<IResult> RemoveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (device.Wallpaper.Length == 0)
        {
            return Results.NoContent();
        }

        var previous = device.Wallpaper;
        device.Wallpaper = "";
        await audit.SaveAsync(db, AuditInfo.By(admin, "device.update",
            $"{admin.Name} tirou o papel de parede de {device.Label}", "device", device.Id), bumpConfig: true, ct);
        DeleteFile(Folder(db), previous);
        return Results.NoContent();
    }

    private static async Task<IResult> PanelGetAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        return Serve(db, device);
    }

    private static IResult TerminalGetAsync(HttpContext context, PontualDb db)
    {
        var device = context.Device();
        return device.Approved ? Serve(db, device) : throw ApiError.NotFound("Este computador não tem papel de parede.");
    }

    private static IResult Serve(PontualDb db, Device device)
    {
        // O nome gravado é sempre o que esta Central gerou (id + carimbo + extensão); mesmo assim, nada de caminho.
        var name = Path.GetFileName(device.Wallpaper);
        var path = name.Length == 0 ? null : Path.Combine(Folder(db), name);
        if (path is null || !File.Exists(path) || !Types.TryGetValue(Path.GetExtension(name), out var type))
        {
            throw ApiError.NotFound("Este computador não tem papel de parede.");
        }

        return Results.File(path, type);
    }

    /// <summary>Chamado quando a máquina é removida da Central: a imagem vai junto.</summary>
    public static void DeleteFile(PontualDb db, Device device) => DeleteFile(Folder(db), device.Wallpaper);

    private static void DeleteFile(string folder, string name)
    {
        name = Path.GetFileName(name);
        if (name.Length == 0)
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(folder, name));
        }
        catch (IOException)
        {
            // Arquivo em uso: sobra um arquivo sem dono na pasta, sem efeito na base.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Folder(PontualDb db)
    {
        var source = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        return Path.Combine(Path.GetDirectoryName(source) ?? AppContext.BaseDirectory, "papeis-de-parede");
    }

    /// <summary>Confere o cabeçalho do arquivo: a extensão sozinha não prova nada.</summary>
    private static bool LooksLikeImage(ReadOnlySpan<byte> bytes, string extension) => extension switch
    {
        ".png" => bytes.Length > 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        ".jpg" or ".jpeg" => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".webp" => bytes.Length > 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };
}
