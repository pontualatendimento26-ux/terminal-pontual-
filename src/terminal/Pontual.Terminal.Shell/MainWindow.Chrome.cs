using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Vigia dos navegadores (Google Chrome, Microsoft Edge, Opera e Mozilla Firefox). A atendente usa o navegador com os
/// perfis dela (um WhatsApp em cada); a cada poucos segundos o Terminal lê o endereço da aba visível de cada janela e,
/// se o domínio não é operacional, avisa a Central do TI. A atendente não vê aviso nenhum. O TI decide na Central:
/// liberar (o aviso para) ou bloquear (o domínio entra na política dos navegadores desta conta e a aba é desviada).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan ChromeInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ChromeIntervalLight = TimeSpan.FromSeconds(10);

    /// <summary>A cada 30 s as listas no registro são conferidas de novo: o instalador e a atualização regravam o Edge.</summary>
    private static readonly TimeSpan PolicyCheckInterval = TimeSpan.FromSeconds(30);

    private readonly ChromeWatch _chromeWatch = new();
    private HostPolicy _chromeQuiet = HostPolicy.Empty;
    private HostPolicy _chromeBlocked = HostPolicy.Empty;
    private (string[] Chrome, string[] Edge, string[] Firefox)? _browserLists;
    private DateTime _policyCheckedAt = DateTime.UtcNow;
    private Task<(IReadOnlyList<BrowserAddress> Addresses, IReadOnlyList<BrowserRedirect> Redirected)>? _chromeReading;

    /// <summary>Chamado a cada configuração nova: o que não gera aviso e a lista de bloqueio de cada navegador.</summary>
    private void ApplyChromeConfig(TerminalConfig? config)
    {
        if (config is null)
        {
            return;
        }

        var allowed = config.ChromeAllowed ?? [];
        var blocked = config.ChromeBlocked ?? [];
        // Operacionais da máquina, liberados e bloqueados (o bloqueado é recusado ou desviado) não geram aviso.
        _chromeQuiet = new HostPolicy(config.Items.SelectMany(i => i.Hosts).Concat(allowed).Concat(blocked));
        _chromeBlocked = new HostPolicy(blocked);

        var list = blocked.Select(HostPolicy.NormalizeHost).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // Decisão do dono (27/11/2026): o Edge obedece aos bloqueios do TI, igual ao Chrome (de 24/11 a 27/11 a lista
        // dele ficava vazia). Gravar a lista também apaga o "*" deixado por instalações antigas.
        _browserLists = (list, list, [.. BrowserPolicy.FirefoxPatterns(list)]);
        ApplyBrowserPolicies();
    }

    private void ApplyBrowserPolicies()
    {
        if (_browserLists is not { } lists)
        {
            return;
        }

        BrowserPolicy.Apply("Chrome", BrowserPolicy.ChromeKey, lists.Chrome, create: true);
        // A chave do Edge é criada pelo instalador com bloqueio; numa instalação de teste ela não existe e não é criada.
        BrowserPolicy.Apply("Edge", BrowserPolicy.EdgeKey, lists.Edge, create: false);
        BrowserPolicy.Apply("Firefox", BrowserPolicy.FirefoxKey, lists.Firefox, create: true);
    }

    private async Task ChromeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_lightMode ? ChromeIntervalLight : ChromeInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (DateTime.UtcNow - _policyCheckedAt >= PolicyCheckInterval)
            {
                _policyCheckedAt = DateTime.UtcNow;
                ApplyBrowserPolicies();
            }

            // Uma leitura travada (navegador ocupado) não empilha outra.
            if (_config is null || _state != HeartbeatResponse.StateActive || _chromeReading is { IsCompleted: false })
            {
                continue;
            }

            IReadOnlyList<BrowserAddress> addresses;
            IReadOnlyList<BrowserRedirect> redirected;
            try
            {
                // O bloqueio do TI é garantido aqui também: aba com domínio bloqueado vai para a página de bloqueio,
                // mesmo que a política do navegador não tenha sido gravada ou que a página já estivesse aberta.
                var blocked = _chromeBlocked;
                _chromeReading = Task.Run(() => ChromeReader.Scan(blocked.IsHostAllowed), ct);
                (addresses, redirected) = await _chromeReading.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                Log.Error("ler os navegadores", ex);
                continue;
            }

            // Fica no histórico da máquina ("tentou abrir ... no Opera, que o TI bloqueou"), sem abrir alerta novo.
            foreach (var redirect in redirected.Distinct())
            {
                Log.Info($"{redirect.Browser}: {redirect.Host} bloqueado pelo TI; aba desviada para a página de bloqueio.");
                Record(DeviceEvent.ChromeVisit, redirect.Browser, redirect.Host);
            }

            // O mesmo domínio aberto em dois navegadores conta uma vez, com o nome do primeiro em que foi lido.
            var browserOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var address in addresses)
            {
                if (ChromeWatch.HostFromAddress(address.Address) is { } host)
                {
                    browserOf.TryAdd(host, address.Browser);
                }
            }

            var quiet = _chromeQuiet;
            foreach (var (type, host) in _chromeWatch.Update(browserOf.Keys, quiet.IsHostAllowed, DateTimeOffset.UtcNow))
            {
                Record(type, browserOf.GetValueOrDefault(host, "Navegador"), host);
            }
        }
    }
}
