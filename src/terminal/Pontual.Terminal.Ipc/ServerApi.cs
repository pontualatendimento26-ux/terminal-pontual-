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

    public async Task<HeartbeatResponse> HeartbeatAsync(IEnumerable<string> appsInUse, DateTimeOffset sessionStartedAt, CancellationToken ct = default)
    {
        var body = new { appsInUse = appsInUse.ToArray(), sessionStartedAt = sessionStartedAt.ToUniversalTime() };
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

    /// <summary>Pergunta se o TI abriu a visualização ao vivo da tela desta máquina.</summary>
    public async Task<ScreenRequest> GetScreenRequestAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("device/screen", ct).ConfigureAwait(false);
        return await ReadAsync<ScreenRequest>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Envia uma imagem da tela (JPEG). Devolve se o TI continua vendo; com falso, o terminal para de enviar.</summary>
    public async Task<bool> SendScreenAsync(byte[] jpeg, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var response = await _http.PostAsync("device/screen", content, ct).ConfigureAwait(false);
        return (await ReadAsync<ScreenRequest>(response, ct).ConfigureAwait(false)).Wanted;
    }

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
        return config with { Items = items };
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
