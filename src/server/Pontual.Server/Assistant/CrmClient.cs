using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>Falha ao falar com o CRM; a mensagem já está pronta para a tela.</summary>
public sealed class CrmFailure(string message) : Exception(message);

/// <summary>Endereço e conta do Agente Externo, como estão gravados na Central.</summary>
public sealed record CrmAccess(string Url, string User, string Password);

/// <summary>
/// Fala com a API do Agente Externo do CRM (<c>/api/agente-externo/v1</c>). O token do login vale 24 horas e é
/// guardado em memória por 23; se o CRM recusar o token antes disso, o login é refeito uma vez.
/// </summary>
public sealed class CrmClient
{
    private static readonly TimeSpan TokenLife = TimeSpan.FromHours(23);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _tokenFor = "";
    private string _token = "";
    private DateTime _tokenUntil;

    public CrmClient(TimeProvider time, HttpMessageHandler? handler = null)
    {
        _time = time;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>O acesso gravado, ou nulo se falta endereço, login ou senha.</summary>
    public static async Task<CrmAccess?> AccessAsync(PontualDb db, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking()
            .Where(s => s.Key == TextSetting.CrmUrl || s.Key == TextSetting.CrmUser || s.Key == TextSetting.CrmPassword)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var url = settings.GetValueOrDefault(TextSetting.CrmUrl, "").TrimEnd('/');
        var user = settings.GetValueOrDefault(TextSetting.CrmUser, "");
        var password = AssistantSecrets.Unprotect(settings.GetValueOrDefault(TextSetting.CrmPassword, "")) ?? "";
        return url.Length > 0 && user.Length > 0 && password.Length > 0 ? new CrmAccess(url, user, password) : null;
    }

    /// <summary>Confere o acesso: faz o login e lê as etapas do funil do agente.</summary>
    public async Task<int> TestAsync(CrmAccess access, CancellationToken ct)
    {
        Forget();
        using var response = await SendAsync(access, HttpMethod.Get, "/api/agente-externo/v1/etapas", null, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind == JsonValueKind.Array ? json.GetArrayLength() : 0;
    }

    /// <summary>Manda texto ao cliente da conversa. Devolve o ID da mensagem no WhatsApp, quando o CRM informa.</summary>
    public async Task<string> SendTextAsync(CrmAccess access, string conversationId, string text, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Post, $"/api/agente-externo/v1/conversas/{Uri.EscapeDataString(conversationId)}/mensagens", new { texto = text }, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("id", out var id) ? id.ToString() : "";
    }

    /// <summary>Passa a conversa para uma atendente (o CRM escolhe quem, se não vier o ID).</summary>
    public async Task<string> HandOverAsync(CrmAccess access, string conversationId, string reason, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Put, $"/api/agente-externo/v1/conversas/{Uri.EscapeDataString(conversationId)}/atendimento",
            new { modo = "humano", motivo = reason }, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("responsavel", out var who) && who.ValueKind == JsonValueKind.Object
            && who.TryGetProperty("nome", out var name) ? name.ToString() : "";
    }

    private void Forget()
    {
        _token = "";
        _tokenFor = "";
    }

    private async Task<HttpResponseMessage> SendAsync(CrmAccess access, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(access, ct);
            using var request = new HttpRequestMessage(method, access.Url + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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
                throw new CrmFailure($"O CRM não respondeu ({access.Url}). Confira se ele está no ar.");
            }

            // Token vencido ou trocado no CRM: refaz o login uma vez.
            if (attempt == 0 && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                Forget();
                continue;
            }

            return response;
        }
    }

    private async Task<string> TokenAsync(CrmAccess access, CancellationToken ct)
    {
        var key = $"{access.Url}\n{access.User}\n{access.Password}";
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_tokenFor == key && _token.Length > 0 && now < _tokenUntil)
            {
                return _token;
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync(access.Url + "/login", new { username = access.User, password = access.Password }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new CrmFailure($"O CRM não respondeu ({access.Url}). Confira se ele está no ar.");
            }

            using (response)
            {
                var json = await ReadAsync(response, ct, login: true);
                var token = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                if (token.Length == 0)
                {
                    throw new CrmFailure("O CRM aceitou o login mas não devolveu o token.");
                }

                _token = token;
                _tokenFor = key;
                _tokenUntil = now + TokenLife;
                return token;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>O corpo da resposta, ou a mensagem de erro do CRM em português.</summary>
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct, bool login = false)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(raw.Length == 0 ? "null" : raw);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            json = default;
        }

        var failed = !response.IsSuccessStatusCode
            || (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False);
        if (!failed)
        {
            return json;
        }

        var message = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? ""
            : "";
        if (login)
        {
            throw new CrmFailure(message.Length > 0 ? $"O CRM recusou o login do agente: {message}" : "O CRM recusou o login do agente. Confira o usuário e a senha.");
        }

        throw new CrmFailure(message.Length > 0 ? $"O CRM recusou: {message}" : $"O CRM respondeu com o erro {(int)response.StatusCode}.");
    }
}
