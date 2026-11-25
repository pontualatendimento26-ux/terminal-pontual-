using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pontual.Terminal.Ipc;

/// <summary>O servidor respondeu, mas com erro (ou com algo que não deu para entender).</summary>
public sealed class ServerApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Rotas do terminal (docs/V1-CONTRATO.md, seção 5).</summary>
public sealed class ServerApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ServerApi(string serverUrl, string? token, HttpMessageHandler? handler = null)
    {
        // O servidor fica na rede local: não passa pelo proxy configurado no Windows.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/api/v1/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Falha de rede ou de tempo: o servidor não respondeu. Vale tentar de novo depois.</summary>
    public static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or IOException;

    public async Task<EnrollResponse> EnrollAsync(string code, string hostname, string windows, CancellationToken ct = default)
    {
        var response = await PostAsync<EnrollResponse>("device/enroll", new { code, hostname, windows }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.DeviceId) || string.IsNullOrWhiteSpace(response.Token))
        {
            throw new ServerApiException(HttpStatusCode.OK, "O servidor respondeu ao cadastro sem o identificador ou o token.");
        }

        return response;
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(IEnumerable<string> appsInUse, DateTimeOffset sessionStartedAt, CancellationToken ct = default, string? version = null, string? coverageStatus = null)
    {
        var body = new { appsInUse = appsInUse.ToArray(), sessionStartedAt = sessionStartedAt.ToUniversalTime(), version, coverageStatus };
        var response = await PostAsync<HeartbeatResponse>("device/heartbeat", body, ct).ConfigureAwait(false);
        return response with { State = response.State ?? "" };
    }

    public async Task<TerminalConfig> GetConfigAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("device/config", ct).ConfigureAwait(false);
        var config = await ReadAsync<TerminalConfig>(response, ct).ConfigureAwait(false);
        return Sanitize(config);
    }

    public async Task SendEventsAsync(IReadOnlyList<DeviceEvent> events, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("device/events", new { events }, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Manda a conversa de WhatsApp que está aberta, para a análise da assistente (o servidor emenda e descarta repetições).</summary>
    public async Task SendWhatsAppAsync(string contact, string text, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("device/assistant/whatsapp", new { contact, text }, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Cobertura de ausência: manda a conversa não lida e recebe o que fazer (enviar, esperar ou nada).</summary>
    public Task<CoverageAction> AskCoverageAsync(string contact, string text, CancellationToken ct = default) =>
        PostAsync<CoverageAction>("device/assistant/coverage", new { contact, text }, ct);

    /// <summary>Conta ao servidor se a mensagem da cobertura saiu.</summary>
    public async Task ReportCoverageAsync(Guid id, bool sent, bool holding, string? error, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"device/assistant/coverage/{id}/result", new { sent, holding, error }, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Respostas da cobertura aprovadas na Central, esperando envio.</summary>
    public async Task<IReadOnlyList<CoverageOutgoing>> GetCoverageOutboxAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("device/assistant/coverage/outbox", ct).ConfigureAwait(false);
        return await ReadAsync<List<CoverageOutgoing>>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Pergunta se o TI abriu a visualização ao vivo da tela desta máquina.</summary>
    public async Task<ScreenRequest> GetScreenRequestAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("device/screen", ct).ConfigureAwait(false);
        return await ReadAsync<ScreenRequest>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Envia uma imagem da tela (JPEG). Devolve se o TI continua vendo; com falso, o terminal para de enviar.
    /// <paramref name="controlDebug"/> é um resumo do controle (quantos comandos o SendInput aceitou) que a Central mostra ao TI.</summary>
    public async Task<bool> SendScreenAsync(byte[] jpeg, CancellationToken ct = default, string? controlDebug = null)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/screen") { Content = content };
        if (!string.IsNullOrEmpty(controlDebug))
        {
            request.Headers.TryAddWithoutValidation("X-Control-Debug", controlDebug);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return (await ReadAsync<ScreenRequest>(response, ct).ConfigureAwait(false)).Wanted;
    }

    /// <summary>
    /// Busca os comandos do mouse e do teclado que o TI mandou. A chamada pode ficar alguns segundos
    /// esperando o próximo comando (por isso o tempo-limite próprio, maior que o padrão de 10 s).
    /// <c>Controlling</c> falso na resposta = o controle acabou.
    /// </summary>
    public async Task<ControlBatch> FetchControlAsync(CancellationToken ct = default)
    {
        using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        slow.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, "device/control");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, slow.Token).ConfigureAwait(false);
        return await ReadAsync<ControlBatch>(response, slow.Token).ConfigureAwait(false);
    }

    /// <summary>Pergunta à Central se esta máquina deve gravar a tela, e como.</summary>
    public async Task<RecordingPolicy> GetRecordingPolicyAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("device/recording", ct).ConfigureAwait(false);
        return await ReadAsync<RecordingPolicy>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Envia um trecho gravado (MP4). Devolve se a gravação continua ligada na Central.</summary>
    public async Task<bool> SendRecordingAsync(byte[] video, DateTime startedAtUtc, double seconds, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(video);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var at = Uri.EscapeDataString(startedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var duration = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        using var response = await _http.PostAsync($"device/recording?startedAt={at}&seconds={duration}", content, ct).ConfigureAwait(false);
        return (await ReadAsync<RecordingAnswer>(response, ct).ConfigureAwait(false)).Record;
    }

    private sealed record RecordingAnswer(bool Record);

    /// <summary>
    /// Manda uma parte do vídeo do dia. Devolve quanto a Central já tem: se a parte já tinha chegado
    /// (a resposta anterior se perdeu), a Central diz de onde continuar.
    /// </summary>
    public async Task<long> SendRecordingDayChunkAsync(Guid upload, long offset, long total, byte[] data, int count, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(data, 0, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync($"device/recording/day/{upload}?offset={offset}&total={total}", content, ct).ConfigureAwait(false);
        return await ReceivedAsync(response, ct).ConfigureAwait(false) ?? total;
    }

    /// <summary>Fecha o envio do vídeo do dia. Null = guardado na Central; um número = faltam partes a partir dali.</summary>
    public async Task<long?> FinishRecordingDayAsync(Guid upload, DateTime startedAtUtc, long total, double[][] parts, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"device/recording/day/{upload}/finish",
            new { startedAt = startedAtUtc.ToUniversalTime(), total, parts }, Json, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await ReceivedAsync(response, ct).ConfigureAwait(false) ?? 0;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return null;
    }

    private static async Task<long?> ReceivedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }

        try
        {
            return (await response.Content.ReadFromJsonAsync<RecordingReceived>(Json, ct).ConfigureAwait(false))?.Received;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RecordingReceived(long Received);

    /// <summary>Tira itens incompletos e troca listas ausentes por listas vazias, para o resto do programa não lidar com nulos.</summary>
    public static TerminalConfig Sanitize(TerminalConfig config)
    {
        var items = (config.Items ?? [])
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Target))
            .Select(i => i with
            {
                Kind = i.Kind ?? ConfigItem.KindSite,
                Group = i.Group ?? "",
                Hosts = (i.Hosts ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).ToArray(),
            })
            .ToArray();
        return config with { Items = items, ChromeAllowed = Hosts(config.ChromeAllowed), ChromeBlocked = Hosts(config.ChromeBlocked) };

        static string[] Hosts(IReadOnlyList<string>? raw) =>
            [.. (raw ?? []).Select(HostPolicy.NormalizeHost).OfType<string>().Distinct(StringComparer.Ordinal).Take(2000)];
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                ?? throw new ServerApiException(response.StatusCode, "O servidor mandou uma resposta vazia.");
        }
        catch (JsonException)
        {
            throw new ServerApiException(response.StatusCode, "O servidor mandou uma resposta que o Terminal não entendeu.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct).ConfigureAwait(false);
            message = error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Erro sem corpo JSON (página de erro de um proxy, por exemplo).
        }

        throw new ServerApiException(
            response.StatusCode,
            string.IsNullOrWhiteSpace(message) ? $"O servidor recusou o pedido (erro {(int)response.StatusCode})." : message);
    }

    private sealed record ErrorBody(string? Error);
}
