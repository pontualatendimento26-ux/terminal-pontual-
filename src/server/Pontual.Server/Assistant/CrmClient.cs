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
    private readonly Dictionary<string, JsonElement> _tagIds = new(StringComparer.Ordinal);

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
    public async Task<string> HandOverAsync(CrmAccess access, string conversationId, string reason, string userId, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Put, $"/api/agente-externo/v1/conversas/{Uri.EscapeDataString(conversationId)}/atendimento",
            new { modo = "humano", motivo = reason, usuario_id = userId }, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("responsavel", out var who) && who.ValueKind == JsonValueKind.Object
            && who.TryGetProperty("nome", out var name) ? name.ToString() : "";
    }

    /// <summary>
    /// Marca o cliente com a etiqueta <see cref="CrmAgent.AiTag"/>, pelas rotas que a tela de conversa do CRM usa
    /// (a API do Agente Externo não tem etiqueta). Cria a etiqueta se ela ainda não existe. Marcar de novo não muda nada.
    /// </summary>
    public Task TagAsync(CrmAccess access, string phone, string lineId, CancellationToken ct) =>
        TagAsync(access, phone, lineId, CrmAgent.AiTag, ct);

    /// <summary>Põe (ou, com <paramref name="remove"/>, tira) a etiqueta <paramref name="name"/> do cliente nesta linha; cria a etiqueta se falta.</summary>
    public async Task TagAsync(CrmAccess access, string phone, string lineId, string name, CancellationToken ct, bool remove = false)
    {
        if (phone.Length == 0 || lineId.Length == 0)
        {
            throw new CrmFailure("O aviso do CRM não trouxe o telefone do cliente ou a linha do WhatsApp.");
        }

        var tagId = await TagIdAsync(access, name, ct);
        using var response = await SendAsync(access, HttpMethod.Post, "/api/chat/tags/toggle",
            new { chatId = phone.Contains('@') ? phone : $"{phone}@c.us", botId = lineId, tagId, action = remove ? "remove" : "add" }, ct);
        await ReadAsync(response, ct);
    }

    private async Task<JsonElement> TagIdAsync(CrmAccess access, string name, CancellationToken ct)
    {
        var key = $"{access.Url}|{name}";
        lock (_tagIds)
        {
            if (_tagIds.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        var id = await FindTagAsync(access, name, ct);
        if (id is null)
        {
            var color = name == CrmAgent.AiTag ? "#7c3aed" : TagColors[(int)((uint)StringComparer.Ordinal.GetHashCode(name) % (uint)TagColors.Length)];
            using var created = await SendAsync(access, HttpMethod.Post, "/api/tags", new { name, color, tipo = "chat" }, ct);
            await ReadAsync(created, ct);
            id = await FindTagAsync(access, name, ct) ?? throw new CrmFailure($"O CRM não mostrou a etiqueta \"{name}\" depois de criada.");
        }

        lock (_tagIds)
        {
            _tagIds[key] = id.Value;
        }

        return id.Value;
    }

    private static readonly string[] TagColors = ["#dc2626", "#ea580c", "#ca8a04", "#16a34a", "#0891b2", "#2563eb", "#db2777", "#4b5563"];

    private async Task<JsonElement?> FindTagAsync(CrmAccess access, string wanted, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Get, "/api/tags?tipo=chat", null, ct);
        var json = await ReadAsync(response, ct);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("tags", out var inner))
        {
            json = inner;
        }

        if (json.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var tag in json.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("id", out var id)
                && (tag.TryGetProperty("name", out var name) || tag.TryGetProperty("nome", out name))
                && name.ValueKind == JsonValueKind.String
                && string.Equals(CrmAgent.Fold(name.GetString()?.Trim() ?? ""), CrmAgent.Fold(wanted), StringComparison.Ordinal))
            {
                return id.Clone();
            }
        }

        return null;
    }

    // ---- Rotas da tela de conversa do CRM (bate-papo). Pedem um usuário que veja as conversas de todos (admin).

    /// <summary>As linhas de WhatsApp e o funil (canal) de cada uma.</summary>
    public async Task<IReadOnlyList<(string Id, string Funnel)>> LinesAsync(CrmAccess access, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Get, "/api/sessions", null, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind != JsonValueKind.Array ? [] : json.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("id", out _))
            .Select(s => (s.GetProperty("id").ToString(), s.TryGetProperty("canal_id", out var c) ? c.ToString() : ""))
            .ToList();
    }

    /// <summary>Uma página (20) das conversas cuja última mensagem é do cliente, nestas linhas, da mais nova para a mais velha.</summary>
    public async Task<(JsonElement Chats, bool More)> WaitingChatsAsync(CrmAccess access, IEnumerable<string> lineIds, DateTime fromDay, DateTime toDay, int page, CancellationToken ct)
    {
        var query = $"/api/chat/contacts?page={page}&busca=&botId={Uri.EscapeDataString(string.Join(',', lineIds))}&status=recebidas&vendedores=&tags=&etapas=&ordem=desc"
            + $"&periodo=custom&dataInicio={fromDay:yyyy-MM-dd}&dataFim={toDay:yyyy-MM-dd}";
        using var response = await SendAsync(access, HttpMethod.Get, query, null, ct);
        var json = await ReadAsync(response, ct);
        var chats = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("chats", out var c) && c.ValueKind == JsonValueKind.Array ? c : default;
        var more = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("temMais", out var m) && m.ValueKind == JsonValueKind.True;
        return (chats, more);
    }

    /// <summary>Uma página (20) de todas as conversas destas linhas, em qualquer data e situação, da mais nova para a mais velha.</summary>
    public async Task<(JsonElement Chats, bool More)> ChatsAsync(CrmAccess access, IEnumerable<string> lineIds, int page, CancellationToken ct)
    {
        var query = $"/api/chat/contacts?page={page}&busca=&botId={Uri.EscapeDataString(string.Join(',', lineIds))}&status=todos&vendedores=&tags=&etapas=&ordem=desc";
        using var response = await SendAsync(access, HttpMethod.Get, query, null, ct);
        var json = await ReadAsync(response, ct);
        var chats = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("chats", out var c) && c.ValueKind == JsonValueKind.Array ? c : default;
        var more = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("temMais", out var m) && m.ValueKind == JsonValueKind.True;
        return (chats, more);
    }

    /// <summary>
    /// A etapa em que o cartão do cliente está no funil Atendimento Portabilidade ("Ganho", "Perdido", "Digitação"...).
    /// Vazio se ele não tem cartão, se o cartão está em outro funil ou se o CRM não informou: aí quem decide é a conversa.
    /// </summary>
    public async Task<string> StageAsync(CrmAccess access, string phone, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(access, HttpMethod.Get, $"/api/chat/cliente-funil/{Uri.EscapeDataString(phone)}", null, ct);
            var json = await ReadAsync(response, ct);
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            static string Text(JsonElement scope, string name) =>
                scope.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return CrmAgent.Fold(Text(data, "nome_canal")).Contains("portabilidade") ? Text(data, "nome_coluna").Trim() : "";
        }
        catch (CrmFailure)
        {
            return "";
        }
    }

    /// <summary>O histórico da conversa do cliente nesta linha.</summary>
    public async Task<JsonElement> MessagesAsync(CrmAccess access, string chatId, string botId, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Get,
            $"/api/chat/messages?chatId={Uri.EscapeDataString(chatId)}&botId={Uri.EscapeDataString(botId)}", null, ct);
        var json = await ReadAsync(response, ct);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
    }

    /// <summary>Manda texto ao cliente pela tela de conversa do CRM (sai na linha dele, em nome do usuário da IA).</summary>
    public async Task SendChatAsync(CrmAccess access, string chatId, string botId, string text, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(access, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, access.Url + "/api/chat/send");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = new MultipartFormDataContent { { new StringContent(chatId), "chatId" }, { new StringContent(botId), "botId" }, { new StringContent(text), "message" } };
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new CrmFailure($"O CRM não respondeu ({access.Url}). Confira se ele está no ar.");
            }

            using (response)
            {
                if (attempt == 0 && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Forget();
                    continue;
                }

                await ReadAsync(response, ct);
                return;
            }
        }
    }

    /// <summary>Um usuário do CRM: o ID que a transferência pede e o nome de exibição (o mesmo do campo Responsável).</summary>
    public sealed record User(string Id, string Name);

    /// <summary>Todos os usuários do CRM (<c>GET /usuarios</c> do Agente Externo).</summary>
    public async Task<IReadOnlyList<User>> UsersAsync(CrmAccess access, CancellationToken ct)
    {
        using var response = await SendAsync(access, HttpMethod.Get, "/api/agente-externo/v1/usuarios", null, ct);
        var json = await ReadAsync(response, ct);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("usuarios", out var inner))
        {
            json = inner;
        }

        return json.ValueKind != JsonValueKind.Array ? [] : json.EnumerateArray()
            .Where(u => u.ValueKind == JsonValueKind.Object && u.TryGetProperty("id", out _))
            .Select(u => new User(u.GetProperty("id").ToString(), u.TryGetProperty("nome", out var n) ? n.ToString() : u.TryGetProperty("name", out n) ? n.ToString() : ""))
            .Where(u => u.Id.Length > 0 && u.Name.Length > 0)
            .ToList();
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
