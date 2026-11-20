using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Vigia do Google Chrome. A atendente usa o Chrome com os perfis dela (um WhatsApp em cada); a cada poucos
/// segundos o Terminal lê o endereço da aba visível de cada janela e, se o domínio não é operacional, avisa a
/// Central do TI. A atendente não vê aviso nenhum. O TI decide na Central: liberar (o aviso para) ou bloquear
/// (o domínio entra na política do Chrome desta conta e o Chrome passa a recusá-lo).
/// </summary>
public partial class MainWindow
{
    private const string ChromeItemName = "Google Chrome";
    private static readonly TimeSpan ChromeInterval = TimeSpan.FromSeconds(3);

    private readonly ChromeWatch _chromeWatch = new();
    private HostPolicy _chromeQuiet = HostPolicy.Empty;
    private HostPolicy _chromeBlocked = HostPolicy.Empty;
    private string[]? _chromeBlockedApplied;
    private Task<(IReadOnlyList<string> Addresses, IReadOnlyList<string> Redirected)>? _chromeReading;

    /// <summary>Chamado a cada configuração nova: o que não gera aviso e a lista de bloqueio do Chrome.</summary>
    private void ApplyChromeConfig(TerminalConfig? config)
    {
        if (config is null)
        {
            return;
        }

        var allowed = config.ChromeAllowed ?? [];
        var blocked = config.ChromeBlocked ?? [];
        // Operacionais da máquina, liberados e bloqueados (o bloqueado o próprio Chrome recusa) não geram aviso.
        _chromeQuiet = new HostPolicy(config.Items.SelectMany(i => i.Hosts).Concat(allowed).Concat(blocked));
        _chromeBlocked = new HostPolicy(blocked);

        var list = blocked.Select(HostPolicy.NormalizeHost).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_chromeBlockedApplied is null || !list.SequenceEqual(_chromeBlockedApplied))
        {
            _chromeBlockedApplied = ChromePolicy.ApplyBlocklist(list) ? list : null;
        }
    }

    private async Task ChromeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ChromeInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Uma leitura travada (Chrome ocupado) não empilha outra.
            if (_config is null || _state != HeartbeatResponse.StateActive || _chromeReading is { IsCompleted: false })
            {
                continue;
            }

            IReadOnlyList<string> addresses;
            IReadOnlyList<string> redirected;
            try
            {
                // O bloqueio do TI é garantido aqui também: aba com domínio bloqueado vai para a página de bloqueio,
                // mesmo que a política do Chrome não tenha sido gravada ou que a página já estivesse aberta.
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
                Log.Error("ler o Chrome", ex);
                continue;
            }

            // Fica no histórico da máquina ("tentou abrir ..., que o TI bloqueou"), sem abrir alerta novo.
            foreach (var host in redirected.Distinct(StringComparer.Ordinal))
            {
                Log.Info($"Chrome: {host} bloqueado pelo TI; aba desviada para a página de bloqueio.");
                Record(DeviceEvent.ChromeVisit, ChromeItemName, host);
            }

            var quiet = _chromeQuiet;
            var hosts = addresses.Select(ChromeWatch.HostFromAddress).OfType<string>();
            foreach (var (type, host) in _chromeWatch.Update(hosts, quiet.IsHostAllowed, DateTimeOffset.UtcNow))
            {
                Record(type, ChromeItemName, host);
            }
        }
    }
}
