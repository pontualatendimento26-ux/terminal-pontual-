using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Effects;
using Microsoft.Web.WebView2.Core;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Modo leve (pedido do dono, 25/11/2026, caso Clayton): o TI liga por computador na Central
/// (<see cref="TerminalConfig.LightMode"/>). As abas paradas descansam, as vigias ficam mais espaçadas e as sombras somem;
/// a gravação menor vem pela regra da própria gravação (<see cref="RecordingPolicy"/>).
/// WhatsApp e CRM nunca dormem: só liberam memória e continuam recebendo mensagens.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan WindowsInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan WindowsIntervalLight = TimeSpan.FromSeconds(3);

    private volatile bool _lightMode;
    private Effect? _toastShadow;

    /// <summary>Chamado a cada configuração nova.</summary>
    private void ApplyLightMode(TerminalConfig? config)
    {
        // Sem configuração (máquina parada pelo TI), fica como estava: as abas fecham de qualquer jeito.
        if (config is null || config.LightMode == _lightMode)
        {
            return;
        }

        _lightMode = config.LightMode;
        Log.Info(_lightMode ? "Modo leve ligado" : "Modo leve desligado");
        _windowsTimer.Interval = _lightMode ? WindowsIntervalLight : WindowsInterval;

        if (Toast.Child is UIElement toast)
        {
            _toastShadow ??= toast.Effect;
            toast.Effect = _lightMode ? null : _toastShadow;
        }

        if (!_lightMode)
        {
            foreach (var tab in _tabs)
            {
                Wake(tab);
            }
        }
    }

    /// <summary>Chamado quando a disposição das abas muda: marca quando a aba saiu da tela e acorda a que voltou.</summary>
    private void TrackRest(SiteTab tab)
    {
        if (tab.IsSelected)
        {
            tab.HiddenSince = null;
            Wake(tab);
        }
        else
        {
            tab.HiddenSince ??= DateTime.UtcNow;
        }
    }

    /// <summary>A cada 5 segundos (relógio): põe para descansar as abas paradas.</summary>
    private void RestIdleTabs()
    {
        // Janela de PDF aberta por um site conversa com a aba que a abriu: enquanto houver uma, ninguém descansa.
        if (!_lightMode || _sitePopups.Count > 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var tab in _tabs.ToArray())
        {
            if (tab.IsClosed || tab.Rest != TabRest.None)
            {
                continue;
            }

            switch (LightMode.RestFor(StaysConnected(tab), tab.HiddenSince, now))
            {
                case TabRest.LowMemory:
                    SaveMemory(tab);
                    break;
                case TabRest.Asleep:
                    _ = SleepAsync(tab);
                    break;
            }
        }
    }

    /// <summary>Vale o endereço do item e também a página em que a aba está agora (o site pode ter levado ao WhatsApp).</summary>
    private bool StaysConnected(SiteTab tab)
    {
        var hosts = _config?.StayConnected;
        return LightMode.StaysConnected(tab.Name, tab.Target, hosts)
            || (tab.View.Source is { } page && LightMode.StaysConnected(tab.Name, page.AbsoluteUri, hosts));
    }

    private static void SaveMemory(SiteTab tab)
    {
        try
        {
            if (tab.View.CoreWebView2 is { } core)
            {
                tab.Rest = TabRest.LowMemory;
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
        }
        catch (Exception ex) when (IsWebViewFailure(ex))
        {
            Log.Error($"modo leve: economia em {tab.Name}", ex);
        }
    }

    private static async Task SleepAsync(SiteTab tab)
    {
        try
        {
            if (tab.View.CoreWebView2 is not { } core || core.IsDocumentPlayingAudio)
            {
                return;
            }

            // Marca antes de esperar: se a aba voltar à tela no meio do caminho, o Wake zera a marca.
            tab.Rest = TabRest.Asleep;
            var slept = await core.TrySuspendAsync();
            if (tab.IsClosed)
            {
                return;
            }

            if (tab.Rest != TabRest.Asleep)
            {
                if (core.IsSuspended)
                {
                    core.Resume();
                }
            }
            else if (!slept)
            {
                // A página não pode pausar agora (por exemplo, está usando o microfone): tenta de novo daqui a 10 minutos.
                tab.Rest = TabRest.None;
                tab.HiddenSince = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (IsWebViewFailure(ex))
        {
            Log.Error($"modo leve: dormir {tab.Name}", ex);
            tab.Rest = TabRest.None;
            tab.HiddenSince = DateTime.UtcNow;
        }
    }

    private static void Wake(SiteTab tab)
    {
        if (tab.Rest == TabRest.None || tab.IsClosed)
        {
            return;
        }

        tab.Rest = TabRest.None;
        try
        {
            if (tab.View.CoreWebView2 is { } core)
            {
                if (core.IsSuspended)
                {
                    core.Resume();
                }

                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        }
        catch (Exception ex) when (IsWebViewFailure(ex))
        {
            // Ao aparecer na tela o WebView2 também acorda sozinho.
            Log.Error($"modo leve: acordar {tab.Name}", ex);
        }
    }

    /// <summary>WebView2 antigo, já fechado ou com o processo caído: o modo leve nunca derruba o Terminal.</summary>
    private static bool IsWebViewFailure(Exception ex) =>
        ex is InvalidOperationException or COMException or NotImplementedException or ArgumentException;
}
