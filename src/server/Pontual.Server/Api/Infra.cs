using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>Erro previsto da API: vira <c>{ "error": "…" }</c> com o status indicado.</summary>
public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>O que o painel precisa perguntar antes de reenviar ("senha", "justificativa").</summary>
    public string? Need { get; init; }

    public static ApiError BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
    public static ApiError NotFound(string message) => new(StatusCodes.Status404NotFound, message);
    public static ApiError Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static ApiError Forbidden(string message = "Você não tem permissão para isso.") =>
        new(StatusCodes.Status403Forbidden, message);
}

public sealed record ErrorBody(
    string Error,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Need = null);

/// <summary>Marca uma rota do painel. Sem permissão e sem login exigido = rota pública (login, setup).</summary>
public sealed record PanelRoute(Permission? Permission, bool RequireAuth);

/// <summary>Marca uma rota do terminal, autenticada por token Bearer.</summary>
public sealed record TerminalRoute;

public static class RouteExtensions
{
    public static RouteHandlerBuilder Panel(this RouteHandlerBuilder builder, Permission permission) =>
        builder.WithMetadata(new PanelRoute(permission, RequireAuth: true));

    public static RouteHandlerBuilder PanelLoggedIn(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new PanelRoute(null, RequireAuth: true));

    public static RouteHandlerBuilder PanelPublic(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new PanelRoute(null, RequireAuth: false));

    public static RouteHandlerBuilder Terminal(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new TerminalRoute());
}

public static class HttpContextExtensions
{
    public const string AdminKey = "pontual.admin";
    public const string DeviceKey = "pontual.device";

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

    public static Admin Admin(this HttpContext context) =>
        context.Items[AdminKey] as Admin ?? throw new ApiError(StatusCodes.Status401Unauthorized, "Entre no painel para continuar.");

    public static Device Device(this HttpContext context) =>
        context.Items[DeviceKey] as Device ?? throw new ApiError(StatusCodes.Status401Unauthorized, "Máquina não reconhecida.");

    public static void Demand(this HttpContext context, Permission permission)
    {
        if (!PermissionMatrix.IsAllowed(context.Admin().Role, permission))
        {
            throw ApiError.Forbidden();
        }
    }

    /// <summary>Lê o corpo JSON; qualquer problema de formato vira 400 com mensagem em português.</summary>
    public static async Task<T> ReadBodyAsync<T>(this HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw ApiError.BadRequest("Envie os dados em JSON.");
        }

        try
        {
            var body = await context.Request.ReadFromJsonAsync<T>(BodyOptions, context.RequestAborted);
            return body ?? throw ApiError.BadRequest("O corpo da requisição está vazio.");
        }
        catch (JsonException)
        {
            throw ApiError.BadRequest("Os dados enviados não estão no formato esperado.");
        }
    }
}

public static partial class Secrets
{
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Oito caracteres do alfabeto do contrato, sem o hífen.</summary>
    public static string NewEnrollmentCode() => RandomNumberGenerator.GetString(CodeAlphabet, 8);

    public static string FormatEnrollmentCode(string code) => $"{code[..4]}-{code[4..]}";

    /// <summary>Tira hífen e espaços e põe em maiúsculas; nulo se não tiver a forma de um código.</summary>
    public static string? NormalizeEnrollmentCode(string? raw)
    {
        if (raw is null || raw.Length > 32)
        {
            return null;
        }

        var code = new string([.. raw.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
        return code.Length == 8 && code.All(c => CodeAlphabet.Contains(c)) ? code : null;
    }
}

/// <summary>Saneamento e validação de entrada. Nada que vem de fora é gravado sem passar por aqui.</summary>
public static partial class Input
{
    public static readonly string[] Groups = ["Bancos e promotoras", "Comunicação", "Pontual e ferramentas"];

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,180}\.[^@\s.]{2,}$")]
    private static partial Regex EmailPattern();

    /// <summary>Tira espaços das pontas, caracteres de controle e surrogates soltos.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else if (!char.IsSurrogate(c) && !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Texto obrigatório vindo do painel: recusa vazio e acima do limite.</summary>
    public static string Required(string? value, string what, int max)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            throw ApiError.BadRequest($"Informe {what}.");
        }

        if (text.Length > max)
        {
            throw ApiError.BadRequest($"O campo de {what} aceita no máximo {max} caracteres.");
        }

        return text;
    }

    /// <summary>Texto vindo do terminal: nunca recusa, só limpa e corta.</summary>
    public static string Truncated(string? value, int max)
    {
        if (value is { Length: > 4096 })
        {
            value = value[..4096];
        }

        var text = Clean(value);
        return text.Length <= max ? text : Clean(text[..max]);
    }

    public static string Email(string? value)
    {
        var email = Clean(value).ToLowerInvariant();
        if (email.Length > 254 || !EmailPattern().IsMatch(email))
        {
            throw ApiError.BadRequest("Informe um e-mail válido.");
        }

        return email;
    }

    public static string Password(string? value)
    {
        if (value is null || value.Length < 10)
        {
            throw ApiError.BadRequest("A senha precisa ter pelo menos 10 caracteres.");
        }

        if (value.Length > 128)
        {
            throw ApiError.BadRequest("A senha aceita no máximo 128 caracteres.");
        }

        return value;
    }

    public static string Group(string? value)
    {
        var group = Clean(value);
        return Groups.FirstOrDefault(g => g == group)
            ?? throw ApiError.BadRequest("Grupo inválido. Use: " + string.Join(", ", Groups) + ".");
    }

    public static bool IsHostname(string value) => HostnamePattern().IsMatch(value);

    /// <summary>
    /// Aceita um domínio puro ou, por tolerância, um endereço completo, e devolve só o domínio em minúsculas.
    /// </summary>
    public static bool TryHost(string? raw, out string host)
    {
        host = "";
        if (raw is null || raw.Length > 2048)
        {
            return false;
        }

        var value = Clean(raw).ToLowerInvariant();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }

            value = uri.Host;
        }
        else
        {
            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value[..slash];
            }

            var colon = value.LastIndexOf(':');
            if (colon > 0 && value[(colon + 1)..].All(char.IsAsciiDigit))
            {
                value = value[..colon];
            }
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.TrimEnd('.');
        if (!HostPattern().IsMatch(value))
        {
            return false;
        }

        host = value;
        return true;
    }

    public static List<string> Hosts(IReadOnlyList<string?>? raw)
    {
        if (raw is null)
        {
            return [];
        }

        if (raw.Count > 50)
        {
            throw ApiError.BadRequest("Um site aceita no máximo 50 domínios extras.");
        }

        var hosts = new List<string>();
        foreach (var entry in raw)
        {
            if (!TryHost(entry, out var host))
            {
                throw ApiError.BadRequest($"Domínio inválido: {Truncated(entry, 80)}");
            }

            if (!hosts.Contains(host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    /// <summary>Um domínio é permitido se for igual a um da lista ou subdomínio dele.</summary>
    public static bool HostMatches(string host, IEnumerable<string> allowed) =>
        allowed.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal));

    /// <summary>Só http e https, com domínio válido e sem usuário/senha embutidos.</summary>
    public static string SiteUrl(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            throw ApiError.BadRequest("Informe o endereço do site.");
        }

        if (text.Length > 500)
        {
            throw ApiError.BadRequest("O endereço do site aceita no máximo 500 caracteres.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw ApiError.BadRequest("O endereço precisa começar com http:// ou https://.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !HostPattern().IsMatch(uri.Host.ToLowerInvariant()))
        {
            throw ApiError.BadRequest("O endereço do site não é válido.");
        }

        return uri.AbsoluteUri;
    }

    private static readonly HashSet<string> RefusedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regedit", "rundll32", "msiexec", "bash", "wsl", "wt",
    };

    private static readonly char[] ForbiddenPathChars = ['"', '<', '>', '|', '*', '?'];

    /// <summary>
    /// Nome de um aplicativo do computador, como o Terminal informa: "spotify.exe" ou um caminho completo, de que só o
    /// nome do arquivo interessa. Sai em minúsculas. Falso para o que não é um .exe.
    /// </summary>
    public static bool TryExe(string? value, out string exe)
    {
        exe = "";
        var text = Clean(value);
        if (text.Length == 0 || text.Length > 400)
        {
            return false;
        }

        var name = text[(text.LastIndexOfAny(['\\', '/']) + 1)..].Trim();
        if (name.Length < 5 || name.Length > 120 || name.IndexOfAny(ForbiddenPathChars) >= 0 || name.Contains(':')
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name[..^4].Trim(' ', '.').Length == 0)
        {
            return false;
        }

        exe = name.ToLowerInvariant();
        return true;
    }

    /// <summary>Verdadeiro para os programas que o contrato nunca deixa liberar (cmd.exe, powershell.exe…).</summary>
    public static bool IsRefusedProgram(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && RefusedPrograms.Contains(exe[..^4].Trim(' ', '.'));

    public static string ProgramPath(string? value)
    {
        var path = Required(value, "o caminho do programa", 260);

        if (path.IndexOfAny(ForbiddenPathChars) >= 0 || path.LastIndexOf(':') > 1 || path.IndexOf(':') == 0)
        {
            throw ApiError.BadRequest("O caminho do programa tem caracteres inválidos.");
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiError.BadRequest("O caminho precisa terminar em .exe.");
        }

        var fileName = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        // O Windows ignora espaços e pontos no fim do nome: "cmd .exe" não pode passar.
        var stem = fileName[..^4].Trim(' ', '.');
        if (stem.Length == 0)
        {
            throw ApiError.BadRequest("O caminho não indica um programa.");
        }

        if (RefusedPrograms.Contains(stem))
        {
            throw ApiError.BadRequest($"O programa {stem.ToLowerInvariant()}.exe nunca pode ser liberado.");
        }

        return path;
    }
}

public static class Roles
{
    public static string Code(Role role) => role switch
    {
        Role.SuperAdmin => "superadmin",
        Role.Admin => "admin",
        Role.Support => "support",
        Role.Auditor => "auditor",
        _ => "unknown",
    };

    public static string Label(Role role) => role switch
    {
        Role.SuperAdmin => "superadministrador",
        Role.Admin => "administrador",
        Role.Support => "suporte",
        Role.Auditor => "auditor",
        _ => "desconhecido",
    };

    public static Role Parse(string? code) => code switch
    {
        "superadmin" => Role.SuperAdmin,
        "admin" => Role.Admin,
        "support" => Role.Support,
        "auditor" => Role.Auditor,
        _ => throw ApiError.BadRequest("Função inválida. Use superadmin, admin, support ou auditor."),
    };
}
