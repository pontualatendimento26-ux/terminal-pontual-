using System.Text.RegularExpressions;

namespace Pontual.Terminal.Ipc;

/// <summary>
/// Decide o que avisar ao TI a partir dos endereços lidos na barra do Google Chrome a cada volta.
/// Um domínio não operacional gera <see cref="DeviceEvent.ChromeVisit"/> quando aparece e
/// <see cref="DeviceEvent.ChromeStay"/> a cada minuto em que continua na tela. Sumido por alguns minutos,
/// a próxima aparição conta como visita nova. Classe pura: não conhece o Chrome nem a rede.
/// </summary>
public sealed partial class ChromeWatch
{
    public static readonly TimeSpan StayInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(3);
    /// <summary>Intervalo fora da tela que não conta como "continuou aberto".</summary>
    public static readonly TimeSpan StayGap = TimeSpan.FromSeconds(30);
    private const int MaxTracked = 200;

    private readonly Dictionary<string, (DateTimeOffset LastSeen, DateTimeOffset LastReport)> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Domínio do texto da barra de endereço ("web.whatsapp.com/", "https://x.com.br/a?b"), ou <c>null</c> para
    /// o que não é um site: páginas do Chrome, texto de busca ainda não enviado, nomes sem ponto.
    /// </summary>
    public static string? HostFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var text = address.Trim();
        if (text.Length > 2048 || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        // "chrome://", "about:blank", "mailto:", "data:" e afins. "site.com:8080" (porta) continua valendo.
        if (!text.Contains("://", StringComparison.Ordinal) && OtherScheme().IsMatch(text))
        {
            return null;
        }

        var host = HostPolicy.NormalizeHost(text);
        if (host is null)
        {
            return null;
        }

        return host.Contains('.') || System.Net.IPAddress.TryParse(host, out _) ? host : null;
    }

    /// <summary>Recebe os domínios na tela agora e devolve os eventos a enviar (tipo, domínio).</summary>
    public IReadOnlyList<(string Type, string Host)> Update(IEnumerable<string> hosts, Func<string, bool> isQuiet, DateTimeOffset now)
    {
        var events = new List<(string, string)>();
        foreach (var host in hosts.Distinct(StringComparer.Ordinal))
        {
            if (isQuiet(host))
            {
                continue;
            }

            if (!_seen.TryGetValue(host, out var state))
            {
                if (_seen.Count >= MaxTracked)
                {
                    continue;
                }

                _seen[host] = (now, now);
                events.Add((DeviceEvent.ChromeVisit, host));
                continue;
            }

            // Saiu da tela e voltou antes de virar visita nova: os minutos recomeçam a contar daqui.
            var lastReport = now - state.LastSeen > StayGap ? now : state.LastReport;
            if (now - lastReport >= StayInterval)
            {
                events.Add((DeviceEvent.ChromeStay, host));
                lastReport = now;
            }

            _seen[host] = (now, lastReport);
        }

        foreach (var gone in _seen.Where(p => now - p.Value.LastSeen > ForgetAfter).Select(p => p.Key).ToArray())
        {
            _seen.Remove(gone);
        }

        return events;
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:(?!\d)")]
    private static partial Regex OtherScheme();
}
