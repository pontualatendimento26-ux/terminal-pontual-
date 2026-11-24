using System.Collections.Concurrent;
using System.Text.Json;

namespace Pontual.Server.Api;

/// <summary>
/// Atualização do Terminal só para um computador (24/11/2026): o pacote fica em wwwroot/baixar/so-para/&lt;máquina&gt;/ e só
/// aquele computador o recebe no botão Atualizar. O pedido de atualização não leva token, então a máquina é reconhecida pelo
/// IP do último sinal autenticado dela. Serve para escolher o pacote, não para proteger: o pacote é assinado como os outros.
/// Quando a versão geral alcança a da máquina, a geral volta a valer sozinha.
/// </summary>
public static class DevicePackage
{
    private static readonly ConcurrentDictionary<string, string> Hosts = new();

    public static void Remember(HttpContext context, string hostname)
    {
        if (context.Connection.RemoteIpAddress is { } ip)
        {
            Hosts[ip.ToString()] = hostname;
        }
    }

    /// <summary>Caminho do pacote da máquina, ou <c>null</c> para o pacote geral.</summary>
    public static string? PathFor(HttpContext context, string webRoot)
    {
        var file = context.Request.Path.Value switch
        {
            "/baixar/atualizacao/versao.json" => "versao.json",
            "/baixar/atualizacao/terminal.zip" => "terminal.zip",
            _ => null,
        };
        if (file is null || context.Connection.RemoteIpAddress is not { } ip || !Hosts.TryGetValue(ip.ToString(), out var host))
        {
            return null;
        }

        return Choose(webRoot, host) ? $"/baixar/so-para/{host}/{file}" : null;
    }

    /// <summary>Verdadeiro se há pacote completo da máquina com versão maior que a geral.</summary>
    public static bool Choose(string webRoot, string host)
    {
        if (host.Length == 0 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        var folder = Path.Combine(webRoot, "baixar", "so-para", host);
        return File.Exists(Path.Combine(folder, "terminal.zip"))
            && ReadVersion(Path.Combine(folder, "versao.json")) is { } own
            && (ReadVersion(Path.Combine(webRoot, "baixar", "atualizacao", "versao.json")) is not { } general || own > general);
    }

    private static Version? ReadVersion(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return Version.TryParse(doc.RootElement.GetProperty("versao").GetString(), out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
