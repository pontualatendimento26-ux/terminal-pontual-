using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Gravação da tela das atendentes. Regras da casa:
/// o Terminal grava o dia (7h às 18h) e, quando o dia fecha, manda UM vídeo do dia, em partes; o vídeo é cifrado (AES-256-GCM) e guardado
/// numa pasta FORA da pasta da Central (para não entrar nas cópias de segurança de cada atualização);
/// tudo é apagado depois de 3 meses, ou antes se o disco estiver acabando; e cada abertura das gravações de um dia fica na auditoria.
/// </summary>
public sealed class RecordingStore(IConfiguration configuration, ILogger<RecordingStore> logger)
{
    /// <summary>Prazo de guarda pedido pelo dono: 3 meses.</summary>
    public const int RetentionMonths = 3;

    /// <summary>
    /// Abaixo disso de espaço livre, as gravações mais antigas são apagadas antes do prazo.
    /// Padrão 15 GB; <c>Recordings:MinFreeGB</c> troca (os testes usam 0).
    /// </summary>
    public long MinFreeBytes => (long)(double.TryParse(configuration["Recordings:MinFreeGB"], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb >= 0 ? gb : 15) * 1024 * 1024 * 1024;

    private static readonly byte[] Magic = "PGR1"u8.ToArray();

    /// <summary>Vídeo do dia: cifrado em blocos de 1 MB, para a Central pular para qualquer horário sem abrir o arquivo todo.</summary>
    private static readonly byte[] MagicBlocks = "PGR2"u8.ToArray();
    public const int BlockSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private byte[]? _key;

    /// <summary>
    /// Pasta das gravações: <c>Recordings:Path</c> na configuração ou, sem ela, <c>Pontual-Gravacoes</c> na raiz do disco
    /// da Central (no PC da Pontual, F:\Pontual-Gravacoes, que tem espaço; o C: está quase cheio).
    /// </summary>
    public string Root
    {
        get
        {
            var configured = configuration["Recordings:Path"];
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory, "Pontual-Gravacoes")
                : Path.GetFullPath(configured);
        }
    }

    public long FreeBytes()
    {
        try
        {
            Directory.CreateDirectory(Root);
            return new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    public async Task SaveAsync(PontualDb db, ScreenRecording recording, byte[] video, CancellationToken ct)
    {
        var key = await KeyAsync(db, ct);
        recording.File = FileName(recording);
        var path = Path.Combine(Root, recording.File);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var output = new byte[Magic.Length + NonceSize + TagSize + video.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, video, output.AsSpan(Magic.Length + NonceSize + TagSize), output.AsSpan(Magic.Length + NonceSize, TagSize), recording.Id.ToByteArray());
        }

        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, output, ct);
        File.Move(temporary, path, overwrite: true);
    }

    private static string FileName(ScreenRecording recording)
    {
        var local = DateTime.SpecifyKind(recording.StartedAt, DateTimeKind.Utc).ToLocalTime();
        return Path.Combine(local.ToString("yyyy-MM", CultureInfo.InvariantCulture), local.ToString("dd", CultureInfo.InvariantCulture),
            recording.DeviceId.ToString("N"), $"{local:HHmmss}-{recording.Id:N}.pgr");
    }

    /// <summary>Onde o vídeo do dia vai sendo montado enquanto as partes chegam.</summary>
    public string IncomingPath(Guid deviceId, Guid uploadId) => Path.Combine(Root, "_recebendo", $"{deviceId:N}_{uploadId:N}.part");

    /// <summary>Cifra o vídeo do dia (já completo em <paramref name="plainPath"/>) em blocos e guarda na pasta das gravações.</summary>
    public async Task SaveFileAsync(PontualDb db, ScreenRecording recording, string plainPath, CancellationToken ct)
    {
        var key = await KeyAsync(db, ct);
        recording.File = FileName(recording);
        var path = Path.Combine(Root, recording.File);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var input = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            var header = new byte[8];
            MagicBlocks.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), BlockSize);
            await output.WriteAsync(header, ct);

            using var aes = new AesGcm(key, TagSize);
            var plain = new byte[BlockSize];
            var cipher = new byte[BlockSize];
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var total = input.Length;
            long done = 0, index = 0;
            do
            {
                var count = (int)Math.Min(BlockSize, total - done);
                await input.ReadExactlyAsync(plain.AsMemory(0, count), ct);
                RandomNumberGenerator.Fill(nonce);
                aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, BlockData(recording.Id, index, done + count == total));
                await output.WriteAsync(nonce, ct);
                await output.WriteAsync(tag, ct);
                await output.WriteAsync(cipher.AsMemory(0, count), ct);
                done += count;
                index++;
            }
            while (done < total);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Dados autenticados de cada bloco: a gravação, a posição e se é o último (ninguém troca nem corta blocos sem ser percebido).</summary>
    internal static byte[] BlockData(Guid id, long index, bool last)
    {
        var data = new byte[25];
        id.TryWriteBytes(data);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), index);
        data[24] = last ? (byte)1 : (byte)0;
        return data;
    }

    /// <summary>Abre o vídeo decifrado para tocar: o do dia é lido bloco a bloco; o trecho antigo de 1 minuto, inteiro na memória.</summary>
    public async Task<Stream> OpenAsync(PontualDb db, ScreenRecording recording, CancellationToken ct)
    {
        var path = Path.Combine(Root, recording.File);
        if (!File.Exists(path))
        {
            throw ApiError.NotFound("O arquivo desta gravação não está mais na pasta das gravações.");
        }

        var head = new byte[4];
        await using (var probe = File.OpenRead(path))
        {
            await probe.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        }

        if (!head.AsSpan().SequenceEqual(MagicBlocks))
        {
            return new MemoryStream(await ReadAsync(db, recording, ct), writable: false);
        }

        var key = await KeyAsync(db, ct);
        try
        {
            return new RecordingReadStream(path, key, recording.Id);
        }
        catch (InvalidDataException)
        {
            throw ApiError.Conflict("O arquivo desta gravação está danificado.");
        }
    }

    public async Task<byte[]> ReadAsync(PontualDb db, ScreenRecording recording, CancellationToken ct)
    {
        var key = await KeyAsync(db, ct);
        var path = Path.Combine(Root, recording.File);
        if (!File.Exists(path))
        {
            throw ApiError.NotFound("O arquivo deste trecho não está mais na pasta das gravações.");
        }

        var data = await File.ReadAllBytesAsync(path, ct);
        if (data.Length < Magic.Length + NonceSize + TagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw ApiError.Conflict("O arquivo deste trecho está danificado.");
        }

        var video = new byte[data.Length - Magic.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(Magic.Length, NonceSize), data.AsSpan(Magic.Length + NonceSize + TagSize),
                data.AsSpan(Magic.Length + NonceSize, TagSize), video, recording.Id.ToByteArray());
        }
        catch (CryptographicException)
        {
            throw ApiError.Conflict("O arquivo deste trecho está danificado ou foi cifrado com outra chave.");
        }

        return video;
    }

    /// <summary>
    /// Apaga o que passou de 3 meses e, se o disco estiver com menos de <see cref="MinFreeBytes"/> livres, os trechos
    /// mais antigos até voltar ao mínimo. Devolve quantos trechos foram apagados.
    /// </summary>
    public async Task<int> PurgeAsync(PontualDb db, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddMonths(-RetentionMonths);
        var removed = 0;
        RemoveStaleUploads(now);
        while (true)
        {
            var old = await db.ScreenRecordings.Where(r => r.StartedAt < cutoff).OrderBy(r => r.StartedAt).Take(500).ToListAsync(ct);
            if (old.Count == 0)
            {
                break;
            }

            removed += await DeleteAsync(db, old, ct);
        }

        var floor = MinFreeBytes;
        while (FreeBytes() is var free && free >= 0 && free < floor)
        {
            var oldest = await db.ScreenRecordings.OrderBy(r => r.StartedAt).Take(200).ToListAsync(ct);
            if (oldest.Count == 0)
            {
                break;
            }

            logger.LogWarning("Pouco espaço no disco das gravações: apagando {Count} trechos mais antigos antes do prazo.", oldest.Count);
            removed += await DeleteAsync(db, oldest, ct);
        }

        if (removed > 0)
        {
            RemoveEmptyFolders(Root);
        }

        return removed;
    }

    private async Task<int> DeleteAsync(PontualDb db, List<ScreenRecording> recordings, CancellationToken ct)
    {
        foreach (var recording in recordings)
        {
            try
            {
                File.Delete(Path.Combine(Root, recording.File));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Não foi possível apagar o arquivo da gravação {File}.", recording.File);
            }
        }

        db.ScreenRecordings.RemoveRange(recordings);
        await db.SaveChangesAsync(ct);
        return recordings.Count;
    }

    /// <summary>Vídeo do dia que começou a chegar e o Terminal não terminou de mandar em 3 dias: não vem mais.</summary>
    private void RemoveStaleUploads(DateTime now)
    {
        var folder = Path.Combine(Root, "_recebendo");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in new DirectoryInfo(folder).GetFiles("*.part"))
        {
            if (file.LastWriteTimeUtc < now.AddDays(-3))
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // Recebendo agora.
                }
            }
        }
    }

    private static void RemoveEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var child in Directory.GetDirectories(folder))
        {
            RemoveEmptyFolders(child);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(child).Any())
                {
                    Directory.Delete(child);
                }
            }
            catch (IOException)
            {
                // Outro trecho chegou agora nesta pasta.
            }
        }
    }

    private async Task<byte[]> KeyAsync(PontualDb db, CancellationToken ct)
    {
        if (_key is { } cached)
        {
            return cached;
        }

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_key is { } again)
            {
                return again;
            }

            var stored = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.RecordingKey, ct);
            if (stored is null)
            {
                var fresh = RandomNumberGenerator.GetBytes(32);
                db.TextSettings.Add(new TextSetting { Key = TextSetting.RecordingKey, Value = AssistantSecrets.Protect(Convert.ToBase64String(fresh)) });
                await db.SaveChangesAsync(ct);
                return _key = fresh;
            }

            // Banco levado para outro computador: o Windows não abre a chave, e os vídeos antigos ficam ilegíveis.
            // A chave NÃO é trocada sozinha, para não perder de vez o que ainda poderia ser recuperado na máquina original.
            var plain = AssistantSecrets.Unprotect(stored.Value)
                ?? throw new ApiError(StatusCodes.Status500InternalServerError, "A chave das gravações não pôde ser aberta neste computador.");
            return _key = Convert.FromBase64String(plain);
        }
        finally
        {
            _keyLock.Release();
        }
    }
}

/// <summary>Confere a cada hora o prazo das gravações e o espaço livre no disco.</summary>
public sealed class RecordingCleanup(IServiceProvider services, RecordingStore store, TimeProvider time, ILogger<RecordingCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var removed = await store.PurgeAsync(scope.ServiceProvider.GetRequiredService<PontualDb>(), time.GetUtcNow().UtcDateTime, stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation("Gravações: {Count} trechos antigos apagados.", removed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A limpeza das gravações falhou; tenta de novo em uma hora.");
                }

                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Servidor encerrando.
        }
    }
}

public static class RecordingEndpoints
{
    /// <summary>Um trecho de 1 minuto fica perto de 1 MB; o teto cobre telas muito movimentadas e trechos mais longos.</summary>
    public const int MaxVideoBytes = 40 * 1024 * 1024;

    /// <summary>O vídeo do dia chega em partes de até 8 MB (cada pedido do Terminal tem 10 s).</summary>
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    /// <summary>Teto do vídeo de um dia (o normal fica entre 120 e 250 MB).</summary>
    public const long MaxDayBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Fim do envio do vídeo do dia. <c>Parts</c>: [[início em segundos no vídeo, duração], ...].</summary>
    public sealed record DayFinish(DateTime StartedAt, long Total, double[][]? Parts);

    /// <summary>
    /// Como o Terminal grava: 1 quadro por segundo, até 1920 pixels de largura, qualidade 50, trechos de 60 s.
    /// No modo leve do computador: até 1280 pixels e 1 quadro a cada 2 s (FrameSeconds; Terminal anterior à 2.17.0 ignora).
    /// </summary>
    public sealed record Policy(bool Record, int Fps, int MaxWidth, int Quality, int SegmentSeconds, int StartMinute, int EndMinute, int FrameSeconds = 1);

    public sealed record SettingsBody(bool? Enabled);

    public static void Map(RouteGroupBuilder api)
    {
        // Terminal: pergunta se deve gravar e manda cada trecho pronto.
        api.MapGet("/device/recording", PolicyAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/recording", UploadAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/recording/day/{upload:guid}", DayChunkAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/recording/day/{upload:guid}/finish", DayFinishAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);

        // Painel: ver gravações é leitura de uso individual; ligar e desligar é publicar configuração (senha e motivo).
        api.MapGet("/recordings", OverviewAsync).Panel(Permission.UsageReadIndividual);
        api.MapPut("/recordings/settings", SettingsAsync).Panel(Permission.ConfigPublish);
        api.MapGet("/devices/{id:guid}/recordings/days", DaysAsync).Panel(Permission.UsageReadIndividual);
        api.MapGet("/devices/{id:guid}/recordings", ListAsync).Panel(Permission.UsageReadIndividual);
        api.MapGet("/recordings/{id:guid}/video", VideoAsync).Panel(Permission.UsageReadIndividual);
    }

    public static async Task<bool> EnabledAsync(PontualDb db, CancellationToken ct) =>
        await db.TextSettings.AsNoTracking().Where(s => s.Key == TextSetting.RecordingEnabled).Select(s => s.Value).FirstOrDefaultAsync(ct) != "0";

    private static async Task<IResult> PolicyAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var device = context.Device();
        var record = device.Approved && await EnabledAsync(db, ct);
        return Results.Ok(new Policy(record, Fps: 1, MaxWidth: device.LightMode ? 1280 : 1920, Quality: 50, SegmentSeconds: 60,
            StartMinute: 7 * 60, EndMinute: 18 * 60, FrameSeconds: device.LightMode ? 2 : 1));
    }

    private static async Task<IResult> UploadAsync(HttpContext context, PontualDb db, RecordingStore store, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved)
        {
            return Results.Ok(new { record = false });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var query = context.Request.Query;
        if (!DateTime.TryParse(query["startedAt"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt)
            || startedAt > now.AddMinutes(10) || startedAt < now.AddMonths(-RecordingStore.RetentionMonths))
        {
            throw ApiError.BadRequest("Horário do trecho inválido.");
        }

        if (!double.TryParse(query["seconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 3600)
        {
            throw ApiError.BadRequest("Duração do trecho inválida.");
        }

        // Reenvio do mesmo trecho (a resposta anterior se perdeu na rede): já está guardado.
        if (await db.ScreenRecordings.AnyAsync(r => r.DeviceId == device.Id && r.StartedAt == startedAt, ct))
        {
            return Results.Ok(new { record = await EnabledAsync(db, ct) });
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxVideoBytes;
        }

        if (context.Request.ContentLength is > MaxVideoBytes)
        {
            throw ApiError.BadRequest("Trecho de vídeo grande demais.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxVideoBytes)
            {
                throw ApiError.BadRequest("Trecho de vídeo grande demais.");
            }

            buffer.Write(chunk, 0, read);
        }

        var video = buffer.ToArray();
        // Só MP4: a Central toca o que chega aqui.
        if (video.Length < 12 || video[4] != (byte)'f' || video[5] != (byte)'t' || video[6] != (byte)'y' || video[7] != (byte)'p')
        {
            throw ApiError.BadRequest("O trecho precisa ser um vídeo MP4.");
        }

        var recording = new ScreenRecording
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            AttendantName = device.AttendantName ?? "",
            StartedAt = startedAt,
            Seconds = seconds,
            Bytes = video.Length,
            ReceivedAt = now,
        };
        await store.SaveAsync(db, recording, video, ct);
        db.ScreenRecordings.Add(recording);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { record = await EnabledAsync(db, ct) });
    }

    /// <summary>
    /// Uma parte do vídeo do dia. <paramref name="offset"/> precisa ser exatamente o que já chegou; se não for
    /// (a resposta anterior se perdeu, por exemplo), a Central responde 409 com o quanto tem, e o Terminal continua dali.
    /// </summary>
    private static async Task<IResult> DayChunkAsync(Guid upload, long offset, long total, HttpContext context, PontualDb db, RecordingStore store, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved)
        {
            throw new ApiError(StatusCodes.Status403Forbidden, "Máquina ainda não aprovada.");
        }

        if (total is <= 0 or > MaxDayBytes || offset < 0 || offset >= total)
        {
            throw ApiError.BadRequest("Tamanho do vídeo do dia inválido.");
        }

        if (await db.ScreenRecordings.AnyAsync(r => r.Id == upload && r.DeviceId == device.Id, ct))
        {
            return Results.Ok(new { received = total });
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxChunkBytes;
        }

        if (context.Request.ContentLength is > MaxChunkBytes)
        {
            throw ApiError.BadRequest("Parte do vídeo grande demais.");
        }

        var path = store.IncomingPath(device.Id, upload);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        if (file.Length != offset)
        {
            return Results.Conflict(new { received = file.Length });
        }

        file.Position = offset;
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (file.Position + read > total)
            {
                file.SetLength(offset);
                throw ApiError.BadRequest("A parte passa do tamanho do vídeo do dia.");
            }

            await file.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return Results.Ok(new { received = file.Length });
    }

    /// <summary>Todas as partes chegaram: confere, cifra e registra o vídeo do dia.</summary>
    private static async Task<IResult> DayFinishAsync(Guid upload, DayFinish body, HttpContext context, PontualDb db, RecordingStore store, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved)
        {
            throw new ApiError(StatusCodes.Status403Forbidden, "Máquina ainda não aprovada.");
        }

        if (await db.ScreenRecordings.AnyAsync(r => r.Id == upload && r.DeviceId == device.Id, ct))
        {
            return Results.Ok(new { record = await EnabledAsync(db, ct) });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var startedAt = body.StartedAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(body.StartedAt, DateTimeKind.Utc) : body.StartedAt.ToUniversalTime();
        if (startedAt > now.AddMinutes(10) || startedAt < now.AddMonths(-RecordingStore.RetentionMonths))
        {
            throw ApiError.BadRequest("Horário do vídeo do dia inválido.");
        }

        var parts = (body.Parts ?? [])
            .Where(p => p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]) && p[0] >= 0 && p[1] > 0 && p[0] + p[1] <= 86400 + 600)
            .Take(5000)
            .Select(p => new[] { Math.Round(p[0], 3), Math.Round(p[1], 3) })
            .OrderBy(p => p[0])
            .ToArray();
        if (parts.Length == 0)
        {
            throw ApiError.BadRequest("O vídeo do dia veio sem trechos.");
        }

        var path = store.IncomingPath(device.Id, upload);
        var length = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (length != body.Total)
        {
            return Results.Conflict(new { received = length });
        }

        var head = new byte[8];
        await using (var probe = File.OpenRead(path))
        {
            await probe.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        }

        // Só MP4: a Central toca o que chega aqui.
        if (head[4] != (byte)'f' || head[5] != (byte)'t' || head[6] != (byte)'y' || head[7] != (byte)'p')
        {
            File.Delete(path);
            throw ApiError.BadRequest("O vídeo do dia precisa ser MP4.");
        }

        var recording = new ScreenRecording
        {
            Id = upload,
            DeviceId = device.Id,
            AttendantName = device.AttendantName ?? "",
            StartedAt = startedAt,
            Seconds = parts.Sum(p => p[1]),
            Bytes = length,
            ReceivedAt = now,
            Parts = JsonSerializer.Serialize(parts),
        };
        await store.SaveFileAsync(db, recording, path, ct);
        db.ScreenRecordings.Add(recording);
        await db.SaveChangesAsync(ct);
        File.Delete(path);
        return Results.Ok(new { record = await EnabledAsync(db, ct) });
    }

    private static double[][] PartsOf(ScreenRecording r)
    {
        if (!string.IsNullOrEmpty(r.Parts))
        {
            try
            {
                if (JsonSerializer.Deserialize<double[][]>(r.Parts) is { Length: > 0 } parts)
                {
                    return parts;
                }
            }
            catch (JsonException)
            {
                // Lista danificada: trata como um trecho só.
            }
        }

        return [[0, r.Seconds]];
    }

    private static async Task<IResult> OverviewAsync(PontualDb db, RecordingStore store, CancellationToken ct)
    {
        var used = await db.ScreenRecordings.SumAsync(r => (long?)r.Bytes, ct) ?? 0;
        var oldest = await db.ScreenRecordings.OrderBy(r => r.StartedAt).Select(r => (DateTime?)r.StartedAt).FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            enabled = await EnabledAsync(db, ct),
            folder = store.Root,
            usedBytes = used,
            freeBytes = store.FreeBytes(),
            oldest = oldest is { } o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : (DateTime?)null,
            retentionMonths = RecordingStore.RetentionMonths,
        });
    }

    private static async Task<IResult> SettingsAsync(SettingsBody body, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        if (body.Enabled is not { } enabled)
        {
            throw ApiError.BadRequest("Informe se a gravação fica ligada.");
        }

        var setting = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.RecordingEnabled, ct);
        if (setting is null)
        {
            setting = new TextSetting { Key = TextSetting.RecordingEnabled };
            db.TextSettings.Add(setting);
        }

        setting.Value = enabled ? "1" : "0";
        await audit.SaveAsync(db, AuditInfo.By(admin, "recording.settings",
            enabled ? $"{admin.Name} ligou a gravação da tela das atendentes" : $"{admin.Name} desligou a gravação da tela das atendentes",
            "setting", null), cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>Dias (no fuso deste computador) que têm gravação da máquina, com o total de minutos.</summary>
    private static async Task<IResult> DaysAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        var rows = await db.ScreenRecordings.AsNoTracking().Where(r => r.DeviceId == id)
            .Select(r => new { r.StartedAt, r.Seconds }).ToListAsync(ct);
        var days = rows
            .GroupBy(r => DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc).ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), parts = g.Count(), minutes = Math.Round(g.Sum(r => r.Seconds) / 60) });
        return Results.Ok(days);
    }

    private static async Task<IResult> ListAsync(Guid id, string? date, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw ApiError.BadRequest("Informe o dia no formato AAAA-MM-DD.");
        }

        var from = DateTime.SpecifyKind(day, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        var parts = await db.ScreenRecordings.AsNoTracking()
            .Where(r => r.DeviceId == id && r.StartedAt >= from && r.StartedAt < to)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);

        await audit.SaveAsync(db, AuditInfo.By(admin, "recording.view",
            $"{admin.Name} abriu as gravações da tela de {device.Label} do dia {day:dd/MM/yyyy}", "device", device.Id), cancellationToken: ct);

        return Results.Ok(parts.Select(r => new
        {
            id = r.Id,
            startedAt = DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc),
            seconds = r.Seconds,
            bytes = r.Bytes,
            attendantName = r.AttendantName,
            parts = PartsOf(r),
        }));
    }

    private static async Task<IResult> VideoAsync(Guid id, HttpContext context, PontualDb db, RecordingStore store, CancellationToken ct)
    {
        var recording = await db.ScreenRecordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ApiError.NotFound("Trecho não encontrado. Ele pode ter passado do prazo de 3 meses.");
        var video = await store.OpenAsync(db, recording, ct);
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(video, "video/mp4", enableRangeProcessing: true);
    }
}

/// <summary>
/// Lê o vídeo do dia decifrando só o bloco de 1 MB que o navegador pede: avançar para as 16h não abre o dia inteiro.
/// Um bloco alterado no disco dá erro de leitura (a autenticação do AES-GCM não confere).
/// </summary>
internal sealed class RecordingReadStream : Stream
{
    private const int Header = 8;
    private const int Overhead = 12 + 16;

    private readonly FileStream _file;
    private readonly AesGcm _aes;
    private readonly Guid _id;
    private readonly int _block;
    private readonly long _blocks;
    private readonly long _length;
    private readonly byte[] _plain;
    private readonly byte[] _record;
    private long _cached = -1;
    private int _cachedLength;
    private long _position;

    public RecordingReadStream(string path, byte[] key, Guid id)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        try
        {
            var head = new byte[Header];
            _file.ReadExactly(head);
            _block = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            if (_block is <= 0 or > 64 * 1024 * 1024)
            {
                throw new InvalidDataException();
            }

            var body = _file.Length - Header;
            var record = (long)_block + Overhead;
            _blocks = Math.Max(1, (body + record - 1) / record);
            _length = body - _blocks * Overhead;
            if (_length < 0)
            {
                throw new InvalidDataException();
            }
        }
        catch (EndOfStreamException)
        {
            _file.Dispose();
            throw new InvalidDataException();
        }
        catch
        {
            _file.Dispose();
            throw;
        }

        _aes = new AesGcm(key, 16);
        _id = id;
        _plain = new byte[_block];
        _record = new byte[_block + Overhead];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var index = _position / _block;
        Load(index);
        var inside = (int)(_position - index * _block);
        var count = Math.Min(buffer.Length, _cachedLength - inside);
        _plain.AsSpan(inside, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    private void Load(long index)
    {
        if (_cached == index)
        {
            return;
        }

        var last = index == _blocks - 1;
        var size = last ? (int)(_length - index * _block) : _block;
        _file.Position = Header + index * ((long)_block + Overhead);
        _file.ReadExactly(_record, 0, size + Overhead);
        try
        {
            _aes.Decrypt(_record.AsSpan(0, 12), _record.AsSpan(Overhead, size), _record.AsSpan(12, 16), _plain.AsSpan(0, size),
                RecordingStore.BlockData(_id, index, last));
        }
        catch (CryptographicException)
        {
            _cached = -1;
            throw new IOException("O arquivo da gravação está danificado.");
        }

        _cached = index;
        _cachedLength = size;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => _length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
            _aes.Dispose();
        }

        base.Dispose(disposing);
    }
}
