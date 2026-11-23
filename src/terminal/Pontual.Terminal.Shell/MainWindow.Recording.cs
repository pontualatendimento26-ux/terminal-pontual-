using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Gravação da tela para a Central: o dia chega na Central como UM vídeo, de uma vez, quando o dia fecha.
/// Quatro partes:
/// a regra (a cada 2 minutos pergunta à Central se grava, como e em que horário; guarda a última resposta para valer sem rede);
/// o gravador (uma linha de execução própria que tira 1 quadro por segundo, só na janela do dia, das 7h às 18h,
/// e guarda o dia na pasta local em pedaços de 1 minuto: se o computador desligar de repente, perde no máximo 1 minuto);
/// o fechamento do dia (às 18h, ou no dia seguinte se o computador desligou antes, junta os pedaços num único .mp4,
/// sem recodificar, com os intervalos de computador desligado no lugar);
/// e o envio (manda o vídeo do dia em partes de 4 MB, continuando de onde parou se a rede cair, e apaga depois que a Central confirma).
/// Sem rede, tudo espera na pasta local, até <see cref="RecordingQueueLimit"/>; acima disso o mais antigo sai.
/// A barra avisa a atendente enquanto a gravação está ligada.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan RecordingPolicyInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RecordingUploadInterval = TimeSpan.FromSeconds(30);
    private const long RecordingQueueLimit = 3L * 1024 * 1024 * 1024;
    private const int RecordingChunkBytes = 4 * 1024 * 1024;
    private const string RecordingStampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const string RecordingDayFormat = "yyyy-MM-dd";

    private static string RecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontual", "Gravacoes");

    private static string RecordingPolicyFile => Path.Combine(RecordingFolder, "regra.json");

    /// <summary>Um subdiretório por dia (data local), com os pedaços de 1 minuto.</summary>
    private static string RecordingDaysFolder => Path.Combine(RecordingFolder, "dias");

    /// <summary>Vídeos do dia já juntados, esperando envio: {id}.mp4 + {id}.json.</summary>
    private static string RecordingSendFolder => Path.Combine(RecordingFolder, "envio");

    private volatile RecordingPolicy? _recordingPolicy;

    /// <summary>Dia (yyyy-MM-dd) do pedaço sendo gravado agora: esse dia não é fechado enquanto grava.</summary>
    private volatile string? _recordingDay;

    private sealed record DayRecording(DateTime StartedAt, double[][] Parts);

    private void StartRecording(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(RecordingDaysFolder);
            Directory.CreateDirectory(RecordingSendFolder);
            // Pedaço que ficou aberto quando o programa foi encerrado à força (sem o índice final, o vídeo não abre),
            // e junção interrompida no meio (o dia ainda está inteiro na pasta e é juntado de novo).
            foreach (var broken in Directory.GetFiles(RecordingFolder, "*.part", SearchOption.AllDirectories)
                         .Concat(Directory.GetFiles(RecordingSendFolder, "*.tmp")))
            {
                File.Delete(broken);
            }

            // Pedaços soltos da versão 2.14.0/2.14.1 (que mandava minuto a minuto): entram no dia deles.
            foreach (var loose in Directory.GetFiles(RecordingFolder, "*.mp4"))
            {
                if (TryParseRecordingName(loose, out var startedAt, out _))
                {
                    var day = Path.Combine(RecordingDaysFolder, startedAt.ToLocalTime().ToString(RecordingDayFormat, CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(day);
                    File.Move(loose, Path.Combine(day, Path.GetFileName(loose)), overwrite: true);
                }
                else
                {
                    File.Delete(loose);
                }
            }

            if (File.Exists(RecordingPolicyFile))
            {
                _recordingPolicy = JsonSerializer.Deserialize<RecordingPolicy>(File.ReadAllText(RecordingPolicyFile));
            }
        }
        catch (Exception ex)
        {
            Log.Error("gravação", ex);
        }

        ShowRecording();
        _ = RecordingPolicyLoopAsync(ct);
        // Fora da linha da tela: juntar o dia e mandar não pode travar a barra.
        _ = Task.Run(() => RecordingUploadLoopAsync(ct), ct);
        new Thread(() => RecordingCaptureLoop(ct)) { IsBackground = true, Name = "Gravação da tela", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void ShowRecording()
    {
        var policy = _recordingPolicy;
        var on = policy is { Record: true };
        var hours = on && policy!.EndMinute > policy.StartMinute && policy.EndMinute < 1440
            ? $" das {Clock(policy.StartMinute)} às {Clock(policy.EndMinute)}"
            : "";
        // Repete a cada consulta (2 min): assim a barra de um monitor ligado depois também recebe o aviso.
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var bar in Bars)
            {
                bar.SetRecording(on, hours);
            }
        });
    }

    private static string Clock(int minute) => minute % 60 == 0 ? $"{minute / 60}h" : $"{minute / 60}h{minute % 60:00}";

    private async Task RecordingPolicyLoopAsync(CancellationToken ct)
    {
        var api = _api!;
        while (!ct.IsCancellationRequested)
        {
            var wait = RecordingPolicyInterval;
            try
            {
                var policy = await api.GetRecordingPolicyAsync(ct);
                if (policy != _recordingPolicy)
                {
                    _recordingPolicy = policy;
                    File.WriteAllText(RecordingPolicyFile, JsonSerializer.Serialize(policy));
                }

                ShowRecording();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ServerApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                // Central de versão anterior, sem gravação.
                wait = TimeSpan.FromMinutes(10);
            }
            catch (Exception ex) when (ServerApi.IsNetworkFailure(ex))
            {
                // Sem rede: vale a última regra recebida.
            }
            catch (Exception ex)
            {
                Log.Error("gravação", ex);
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RecordingCaptureLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_recordingPolicy is { Record: true } policy && policy.InWindow(DateTime.Now))
                {
                    RecordSegment(policy, ct);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Log.Error("gravação", ex);
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                continue;
            }
            finally
            {
                _recordingDay = null;
            }

            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Grava um pedaço até completar a duração, a janela do dia (7h às 18h) fechar, a regra mudar,
    /// a tela mudar de tamanho ou o programa fechar.
    /// </summary>
    private void RecordSegment(RecordingPolicy policy, CancellationToken ct)
    {
        var fps = Math.Clamp(policy.Fps, 1, 5);
        var interval = 1.0 / fps;
        // O último pedaço do dia termina exatamente quando a janela fecha (18h).
        var length = Math.Min(Math.Clamp(policy.SegmentSeconds, 10, 600), policy.SecondsLeft(DateTime.Now));

        var frame = ScreenCapture.Frame(Math.Clamp(policy.MaxWidth, 640, 3840));
        var started = DateTime.UtcNow;
        var dayName = started.ToLocalTime().ToString(RecordingDayFormat, CultureInfo.InvariantCulture);
        _recordingDay = dayName;
        var dayFolder = Path.Combine(RecordingDaysFolder, dayName);
        Directory.CreateDirectory(dayFolder);
        var clock = Stopwatch.StartNew();
        var part = Path.Combine(dayFolder, started.ToString(RecordingStampFormat, CultureInfo.InvariantCulture) + ".part");
        double last = 0;
        var failures = 0;

        using (var writer = new Mp4Writer(part, frame.Width, frame.Height, fps, policy.Quality))
        {
            try
            {
                writer.WriteFrame(frame.Bgra, 0, interval);
                var next = 1;
                while (!ct.IsCancellationRequested && _recordingPolicy == policy)
                {
                    var due = next * interval;
                    if (due >= length)
                    {
                        break;
                    }

                    var sleep = due - clock.Elapsed.TotalSeconds;
                    if (sleep > 0 && ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(sleep)))
                    {
                        break;
                    }

                    // Atrasou (computador ocupado): pula os quadros perdidos em vez de acumular.
                    next = Math.Max(next + 1, (int)(clock.Elapsed.TotalSeconds / interval) + 1);
                    try
                    {
                        frame = ScreenCapture.Frame(Math.Clamp(policy.MaxWidth, 640, 3840));
                        failures = 0;
                    }
                    catch (Exception)
                    {
                        // Tela bloqueada ou trocando de resolução: tenta no próximo quadro; falhando muito, fecha o pedaço.
                        if (++failures >= 10)
                        {
                            break;
                        }

                        continue;
                    }

                    if (frame.Width != writer.Width || frame.Height != writer.Height)
                    {
                        break; // monitor ligado ou desligado: o próximo pedaço começa com o tamanho novo
                    }

                    var at = clock.Elapsed.TotalSeconds;
                    if (at <= last)
                    {
                        continue;
                    }

                    writer.WriteFrame(frame.Bgra, at, interval);
                    last = at;
                }
            }
            finally
            {
                writer.Finish();
            }
        }

        var seconds = Math.Max(interval, last + interval);
        var name = $"{started.ToString(RecordingStampFormat, CultureInfo.InvariantCulture)}_{seconds.ToString("0.###", CultureInfo.InvariantCulture)}.mp4";
        File.Move(part, Path.Combine(dayFolder, name));
    }

    private async Task RecordingUploadLoopAsync(CancellationToken ct)
    {
        var api = _api!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TrimRecordingQueue();
                CloseFinishedDays();
                await SendRecordedDaysAsync(api, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ServerApi.IsNetworkFailure(ex))
            {
                // Sem rede: o vídeo do dia espera na pasta e continua de onde parou.
            }
            catch (ServerApiException)
            {
                // Máquina pendente, removida ou Central antiga: tenta de novo mais tarde.
            }
            catch (Exception ex)
            {
                Log.Error("gravação", ex);
            }

            try
            {
                await Task.Delay(RecordingUploadInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Dia fechado = dia anterior, ou hoje depois do fim da janela (18h). O dia sendo gravado agora nunca é fechado.
    /// </summary>
    private void CloseFinishedDays()
    {
        var now = DateTime.Now;
        var policy = _recordingPolicy;
        var todayClosed = policy is not null && policy.EndMinute > policy.StartMinute && policy.EndMinute < 1440
                          && now.Hour * 60 + now.Minute >= policy.EndMinute;
        foreach (var folder in Directory.GetDirectories(RecordingDaysFolder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            if (!DateTime.TryParseExact(name, RecordingDayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                Directory.Delete(folder, recursive: true);
                continue;
            }

            if (name == _recordingDay || day.Date > now.Date || (day.Date == now.Date && !todayClosed))
            {
                continue;
            }

            JoinDay(folder);
        }
    }

    /// <summary>Junta os pedaços do dia num único vídeo (um por tamanho de tela, se a tela mudou no meio do dia).</summary>
    private static void JoinDay(string folder)
    {
        var segments = Directory.GetFiles(folder, "*.mp4")
            .Select(f => (Path: f, Ok: TryParseRecordingName(f, out var start, out _), Start: start))
            .Where(s => s.Ok)
            .OrderBy(s => s.Start)
            .ToList();

        Mp4Joiner? joiner = null;
        var id = Guid.Empty;
        var origin = DateTime.MinValue;
        try
        {
            foreach (var segment in segments)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (joiner is null)
                    {
                        id = Guid.NewGuid();
                        origin = segment.Start;
                        joiner = new Mp4Joiner(SendPath(id, ".tmp"));
                    }

                    bool appended;
                    try
                    {
                        appended = joiner.TryAppend(segment.Path, (segment.Start - origin).TotalSeconds);
                    }
                    catch (Exception ex)
                    {
                        // Pedaço danificado: fica de fora. Se nada entrou ainda, começa o arquivo de novo.
                        Log.Error("gravação", ex);
                        if (joiner.Parts.Count == 0)
                        {
                            joiner.Dispose();
                            joiner = null;
                            File.Delete(SendPath(id, ".tmp"));
                        }

                        break;
                    }

                    if (appended)
                    {
                        break;
                    }

                    // Tamanho de tela diferente: fecha este vídeo e começa outro com o pedaço.
                    CloseJoined(joiner, id, origin);
                    joiner = null;
                }
            }

            if (joiner is not null)
            {
                var closing = joiner;
                joiner = null;
                CloseJoined(closing, id, origin);
            }
        }
        finally
        {
            joiner?.Dispose();
        }

        Directory.Delete(folder, recursive: true);
    }

    private static void CloseJoined(Mp4Joiner joiner, Guid id, DateTime origin)
    {
        var temporary = SendPath(id, ".tmp");
        try
        {
            joiner.Finish();
        }
        finally
        {
            joiner.Dispose();
        }

        if (joiner.Parts.Count == 0)
        {
            File.Delete(temporary);
            return;
        }

        File.Move(temporary, SendPath(id, ".mp4"), overwrite: true);
        var parts = joiner.Parts.Select(p => new[] { Math.Round(p.Offset, 3), Math.Round(p.Seconds, 3) }).ToArray();
        var meta = SendPath(id, ".json");
        File.WriteAllText(meta + ".tmp", JsonSerializer.Serialize(new DayRecording(origin, parts)));
        File.Move(meta + ".tmp", meta, overwrite: true);
    }

    private static string SendPath(Guid id, string extension) => Path.Combine(RecordingSendFolder, id.ToString("N") + extension);

    private static async Task SendRecordedDaysAsync(ServerApi api, CancellationToken ct)
    {
        foreach (var meta in Directory.GetFiles(RecordingSendFolder, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var video = Path.ChangeExtension(meta, ".mp4");
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(meta), "N", out var id) || !File.Exists(video))
            {
                File.Delete(meta);
                continue;
            }

            var day = JsonSerializer.Deserialize<DayRecording>(await File.ReadAllTextAsync(meta, ct));
            if (day is null || day.Parts is not { Length: > 0 })
            {
                File.Delete(meta);
                File.Delete(video);
                continue;
            }

            try
            {
                await using var file = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                var total = file.Length;
                var buffer = new byte[RecordingChunkBytes];
                long offset = 0;
                while (true)
                {
                    while (offset < total)
                    {
                        file.Position = offset;
                        var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, total - offset)), ct);
                        offset = await api.SendRecordingDayChunkAsync(id, offset, total, buffer, count, ct);
                    }

                    var missing = await api.FinishRecordingDayAsync(id, DateTime.SpecifyKind(day.StartedAt, DateTimeKind.Utc), total, day.Parts, ct);
                    if (missing is null)
                    {
                        break;
                    }

                    if (missing >= total)
                    {
                        throw new IOException("A Central não aceitou o fim do vídeo do dia.");
                    }

                    offset = missing.Value;
                }
            }
            catch (ServerApiException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
            {
                // A Central recusou este vídeo (antigo demais, por exemplo): não adianta tentar de novo.
                Log.Error("gravação", ex);
            }

            File.Delete(video);
            File.Delete(meta);
        }
    }

    /// <summary>Muito tempo sem rede: apaga os vídeos mais antigos para a pasta não encher o disco.</summary>
    private static void TrimRecordingQueue()
    {
        var files = new DirectoryInfo(RecordingFolder).GetFiles("*.mp4", SearchOption.AllDirectories)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= RecordingQueueLimit)
            {
                break;
            }

            total -= file.Length;
            file.Delete();
        }
    }

    private static bool TryParseRecordingName(string file, out DateTime startedAt, out double seconds)
    {
        startedAt = default;
        seconds = 0;
        var parts = Path.GetFileNameWithoutExtension(file).Split('_');
        return parts.Length == 2
            && DateTime.TryParseExact(parts[0], RecordingStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedAt)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && seconds > 0;
    }
}
