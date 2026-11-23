using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>Endereço e token da Central na API de integração do Nexus, como estão gravados na Central.</summary>
public sealed record NexusAccess(string Url, string Token);

/// <summary>Resposta do Nexus: o código HTTP e o corpo JSON (objeto vazio se não veio JSON).</summary>
public sealed record NexusReply(int Status, JsonElement Body)
{
    public string Text(string name) =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>O Nexus não respondeu (rede, endereço ou tempo); a mensagem já está pronta para a tela.</summary>
public sealed class NexusOffline(string message) : Exception(message);

/// <summary>
/// Fala com a API de integração do Nexus (<c>/api/integracao/v1</c>): saúde dos setores, onde o CPF aparece,
/// consulta e acompanhamento. O token vai no cabeçalho Bearer e o Nexus só aceita o IP desta Central.
/// Quem decide o banco em que o cliente passa é o roteiro do Nexus; a Central só repassa.
/// </summary>
public sealed class NexusClient
{
    public const string DefaultUrl = "http://192.168.0.171:3001/api/integracao/v1";
    public static readonly string[] Setores = ["siape", "inss", "exercito"];

    private static readonly TimeSpan HealthLife = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly object _healthLock = new();
    private (string Url, DateTime Until, JsonElement Body)? _health;

    public NexusClient(TimeProvider time, HttpMessageHandler? handler = null)
    {
        _time = time;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // O POST /consultas espera até 10 s a fonte antes de devolver 202.
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    /// <summary>O acesso gravado, ou nulo se falta o token.</summary>
    public static async Task<NexusAccess?> AccessAsync(PontualDb db, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking()
            .Where(s => s.Key == TextSetting.NexusUrl || s.Key == TextSetting.NexusToken)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var url = settings.GetValueOrDefault(TextSetting.NexusUrl, "").TrimEnd('/');
        var token = AssistantSecrets.Unprotect(settings.GetValueOrDefault(TextSetting.NexusToken, "")) ?? "";
        return token.Length > 0 ? new NexusAccess(url.Length > 0 ? url : DefaultUrl, token) : null;
    }

    /// <summary>Estado de cada setor. Guardado por um minuto: o chat pergunta a cada CPF.</summary>
    public async Task<NexusReply> HealthAsync(NexusAccess access, CancellationToken ct, bool fresh = false)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_healthLock)
        {
            if (!fresh && _health is { } cached && cached.Url == access.Url && now < cached.Until)
            {
                return new NexusReply(200, cached.Body);
            }
        }

        var reply = await SendAsync(access, HttpMethod.Get, "/saude", null, ct);
        if (reply.Status == 200)
        {
            lock (_healthLock)
            {
                _health = (access.Url, now + HealthLife, reply.Body);
            }
        }

        return reply;
    }

    /// <summary>Onde o CPF aparece no Nexus. Só fontes gratuitas.</summary>
    public Task<NexusReply> LocateAsync(NexusAccess access, string cpf, CancellationToken ct) =>
        SendAsync(access, HttpMethod.Get, $"/localizar?cpf={cpf}", null, ct);

    /// <summary>Pede a consulta. 200 traz o resultado; 202, o consulta_id para acompanhar.</summary>
    public Task<NexusReply> ConsultAsync(NexusAccess access, string cpf, string setor, string attendantId, string attendantName, bool allowPaid, CancellationToken ct) =>
        SendAsync(access, HttpMethod.Post, "/consultas", new
        {
            cpf,
            setor,
            solicitante = new { id = attendantId, nome = attendantName },
            permitir_consulta_paga = allowPaid,
        }, ct);

    /// <summary>Acompanha uma consulta que respondeu 202.</summary>
    public Task<NexusReply> FollowAsync(NexusAccess access, string consultaId, CancellationToken ct) =>
        SendAsync(access, HttpMethod.Get, $"/consultas/{Uri.EscapeDataString(consultaId)}", null, ct);

    private async Task<NexusReply> SendAsync(NexusAccess access, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, access.Url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new NexusOffline($"O Nexus não respondeu ({access.Url}). Confira se ele está no ar e se a rede alcança o computador dele.");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            JsonElement json;
            try
            {
                using var document = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
                json = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                using var empty = JsonDocument.Parse("{}");
                json = empty.RootElement.Clone();
            }

            return new NexusReply((int)response.StatusCode, json);
        }
    }

    /// <summary>Só os 11 dígitos do CPF, se ele for válido (dígitos verificadores); senão nulo.</summary>
    public static string? NormalizeCpf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return null;
        }

        for (var size = 9; size <= 10; size++)
        {
            var sum = 0;
            for (var i = 0; i < size; i++)
            {
                sum += (digits[i] - '0') * (size + 1 - i);
            }

            var check = sum * 10 % 11 % 10;
            if (check != digits[size] - '0')
            {
                return null;
            }
        }

        return digits;
    }

    public static string FormatCpf(string cpf) =>
        cpf.Length == 11 ? $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}" : cpf;
}
