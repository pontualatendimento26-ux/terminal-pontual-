namespace Pontual.Terminal.Ipc;

public enum NavigationVerdict
{
    /// <summary>O domínio está na lista (ou é <c>about:blank</c>).</summary>
    Allowed,

    /// <summary>Endereço http/https de um domínio fora da lista: observar ou bloquear, conforme o modo.</summary>
    NotListed,

    /// <summary>Esquema que nunca é aceito (file, javascript, data, mailto...), ou endereço ilegível.</summary>
    SchemeBlocked,
}

public readonly record struct NavigationDecision(NavigationVerdict Verdict, string Host);

/// <summary>
/// Política de navegação da página principal de cada aba (docs/V1-CONTRATO.md, "Modo de observação"):
/// um domínio é permitido se for igual a um dos <c>hosts</c> de qualquer item da máquina, ou subdomínio dele.
/// Classe pura: não conhece WebView2 nem rede.
/// </summary>
public sealed class HostPolicy
{
    public static readonly HostPolicy Empty = new([]);

    private readonly HashSet<string> _hosts = new(StringComparer.Ordinal);

    public HostPolicy(IEnumerable<string> allowedHosts)
    {
        foreach (var raw in allowedHosts)
        {
            if (NormalizeHost(raw) is { } host)
            {
                _hosts.Add(host);
            }
        }
    }

    public static HostPolicy FromConfig(TerminalConfig config) => new(config.Items.SelectMany(i => i.Hosts));

    public IReadOnlyCollection<string> Hosts => _hosts;

    public NavigationDecision Evaluate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return new NavigationDecision(NavigationVerdict.SchemeBlocked, "");
        }

        if (uri.Scheme == "about")
        {
            var blank = uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                || uri.AbsoluteUri.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase);
            return new NavigationDecision(blank ? NavigationVerdict.Allowed : NavigationVerdict.SchemeBlocked, "");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new NavigationDecision(NavigationVerdict.SchemeBlocked, "");
        }

        var host = HostOf(uri);
        if (host.Length == 0)
        {
            return new NavigationDecision(NavigationVerdict.SchemeBlocked, "");
        }

        return new NavigationDecision(IsHostAllowed(host) ? NavigationVerdict.Allowed : NavigationVerdict.NotListed, host);
    }

    /// <summary>Recebe um domínio já isolado (sem esquema nem caminho).</summary>
    public bool IsHostAllowed(string? host)
    {
        if (NormalizeHost(host) is not { } candidate)
        {
            return false;
        }

        if (_hosts.Contains(candidate))
        {
            return true;
        }

        // Endereço IP só vale por igualdade: não existe "subdomínio" de IP.
        if (IsIpAddress(candidate))
        {
            return false;
        }

        foreach (var allowed in _hosts)
        {
            // Nome de um rótulo só ("localhost", "intranet") e IP não têm subdomínios.
            if (!allowed.Contains('.') || IsIpAddress(allowed))
            {
                continue;
            }

            // O ponto antes do sufixo é o que separa "x.safra.com.br" de "xsafra.com.br".
            if (candidate.Length > allowed.Length + 1
                && candidate.EndsWith(allowed, StringComparison.Ordinal)
                && candidate[candidate.Length - allowed.Length - 1] == '.')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduz uma entrada de <c>hosts</c> ao domínio em minúsculas, sem porta, caminho ou ponto final.
    /// Aceita também uma URL completa. Devolve <c>null</c> se não der para extrair um domínio.
    /// </summary>
    public static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var host = HostOf(uri);
        return host.Length == 0 ? null : host;
    }

    private static string HostOf(Uri uri)
    {
        try
        {
            return uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return "";
        }
    }

    private static bool IsIpAddress(string host) => System.Net.IPAddress.TryParse(host, out _);
}
