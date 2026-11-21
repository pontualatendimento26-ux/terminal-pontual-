using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Lista "Aplicativos" da tela inicial: a atendente abre os aplicativos do Windows, os instalados e os baixados. O que é
/// programa cadastrado pelo TI ou já liberado abre sem aviso; aplicativo novo instalado abre e vira um pedido de liberação na
/// Central (evento <see cref="DeviceEvent.AppOpened"/>); aplicativo novo da pasta Downloads NÃO abre: só vira o pedido; aplicativo bloqueado pelo TI não abre e é fechado se estiver aberto.
/// A decisão do TI é por nome do .exe (docs/APLICATIVOS-DO-COMPUTADOR.md).
/// </summary>
public partial class MainWindow
{
    private const string AppIdPrefix = "app:";

    private sealed record RunningApp(AppEntry App, Process Process);

    private readonly Dictionary<string, RunningApp> _apps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AppEntry> _appCatalog = new(StringComparer.OrdinalIgnoreCase);
    private Task? _appScan;
    private bool _appsSent;

    private static string AppId(AppEntry app) => AppIdPrefix + app.Path;

    private bool TryRunningApp(string id, out RunningApp app)
    {
        app = null!;
        return id.StartsWith(AppIdPrefix, StringComparison.Ordinal) && _apps.TryGetValue(id[AppIdPrefix.Length..], out app!);
    }

    /// <summary>A tela inicial pediu a lista: varre o computador (fora da linha da interface) e responde com "apps".</summary>
    private async Task SendAppsToHomeAsync()
    {
        try
        {
            if (_appScan is { IsCompleted: false })
            {
                await _appScan;
            }
            else
            {
                _appScan = ScanAppsAsync();
                await _appScan;
            }
        }
        catch (Exception ex)
        {
            Log.Error("listar aplicativos", ex);
        }

        PostAppsToHome();
    }

    private async Task ScanAppsAsync()
    {
        var list = await AppCatalog.ScanAsync(DownloadsFolder());
        _appCatalog = list.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
    }

    private void PostAppsToHome()
    {
        _appsSent = true;
        var apps = _appCatalog.Values
            .Select(a => new { id = AppId(a), name = a.Name, exe = a.Exe, source = a.Source, status = StatusOf(a), open = _apps.ContainsKey(a.Path) })
            .ToArray();
        PostToHome(new { type = "apps", apps });
    }

    private string StatusOf(AppEntry app) => AppPolicy.Decide(app.Path, _config) switch
    {
        AppVerdict.Allowed => "allowed",
        AppVerdict.Blocked => "blocked",
        AppVerdict.Request => "request",
        _ => "refused",
    };

    private void OpenApp(string id)
    {
        if (!id.StartsWith(AppIdPrefix, StringComparison.Ordinal) || !_appCatalog.TryGetValue(id[AppIdPrefix.Length..], out var app))
        {
            return;
        }

        if (_apps.TryGetValue(app.Path, out var running))
        {
            BringToFront(running.Process);
            return;
        }

        var verdict = AppPolicy.Decide(app.Path, _config);
        switch (verdict)
        {
            case AppVerdict.Refused:
                ShowToast($"{app.Name} não pode ser aberto por aqui.");
                return;
            case AppVerdict.Blocked:
                // A tentativa vai para a Central: bloqueado para todas, vira pedido dela; bloqueado só para ela, fica no histórico.
                Log.Info($"Aplicativo bloqueado pelo TI: {app.Path}");
                ShowToast($"O TI bloqueou {app.Name} neste computador.");
                Record(DeviceEvent.AppOpened, app.Name, app.Path);
                return;
            case AppVerdict.Request when app.Source == "downloaded":
                // Baixado da internet e ainda sem decisão do TI: não roda na conta bloqueada; só vira pedido na Central.
                Log.Info($"Aplicativo baixado sem liberação, pedido enviado ao TI: {app.Path}");
                Record(DeviceEvent.AppOpened, app.Name, app.Path);
                ShowToast($"{app.Name} foi baixado da internet e ainda não foi liberado. O TI recebeu o pedido; tente de novo depois da liberação.");
                return;
        }

        if (!File.Exists(app.Path))
        {
            ShowToast($"{app.Name} não está mais neste computador.");
            _appCatalog.Remove(app.Path);
            PostAppsToHome();
            return;
        }

        Process? process = null;
        if (StoreApps.IsStoreLauncher(app.Exe))
        {
            // Calculadora e companhia: o .exe da System32 é só um lançador, que na conta bloqueada não abre nada.
            // O Terminal pede a abertura direto ao Windows; se não der (aplicativo da Loja ausente), usa o lançador.
            if (StoreApps.Activate(app.Exe, out var error) is { } storePid)
            {
                try
                {
                    process = Process.GetProcessById(storePid);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    Log.Info($"{app.Exe}: aplicativo da Loja aberto, mas o processo {storePid} já não existe; segue sem acompanhar.");
                }
            }
            else
            {
                Log.Info($"{app.Exe}: o aplicativo da Loja não abriu pelo gerenciador do Windows ({error}); tentando o lançador {app.Path}.");
            }
        }

        if (process is null)
        {
            try
            {
                process = Process.Start(new ProcessStartInfo(app.Path)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(app.Path) ?? "",
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Log.Error($"abrir {app.Name}", ex);
                process = null;
            }
        }

        if (process is null)
        {
            ShowToast($"Não foi possível abrir {app.Name}. Chame o TI.");
            return;
        }

        if (verdict == AppVerdict.Request)
        {
            Record(DeviceEvent.AppOpened, app.Name, app.Path);
            ShowToast($"{app.Name} foi aberto. O TI recebeu um pedido para liberar este aplicativo.");
        }
        else
        {
            Record(DeviceEvent.Opened, app.Name);
        }

        var run = new RunningApp(app, process);
        _apps[app.Path] = run;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnAppExited(run));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Processo de aplicativo da Loja que não deixa ser acompanhado: fica fora de "Abertos agora".
            Log.Info($"{app.Exe}: não deu para acompanhar o processo ({ex.Message}).");
            _apps.Remove(app.Path);
            process.Dispose();
            return;
        }

        if (process.HasExited)
        {
            // Aplicativos da loja (Calculadora, Bloco de Notas) abrem por um lançador que sai na hora: não há o que acompanhar.
            OnAppExited(run);
            return;
        }

        SendStateToHome();
        PostAppsToHome();
    }

    private void OnAppExited(RunningApp run)
    {
        if (!_apps.TryGetValue(run.App.Path, out var current) || !ReferenceEquals(current, run))
        {
            return;
        }

        _apps.Remove(run.App.Path);
        run.Process.Dispose();
        if (!_cts.IsCancellationRequested)
        {
            Record(DeviceEvent.Closed, run.App.Name);
            SendStateToHome();
            if (_appsSent)
            {
                PostAppsToHome();
            }
        }
    }

    /// <summary>Chamado a cada configuração nova: o que o TI bloqueou é fechado, e a lista na tela inicial muda de situação.</summary>
    private void ApplyAppsConfig(TerminalConfig? config)
    {
        foreach (var run in _apps.Values.Where(r => AppPolicy.Decide(r.App.Path, config) == AppVerdict.Blocked).ToArray())
        {
            Log.Info($"O TI bloqueou {run.App.Exe}; fechando {run.App.Path}.");
            ShowToast($"O TI bloqueou {run.App.Name}. O aplicativo foi fechado.");
            CloseBlockedApp(run.Process);
        }

        if (_appsSent)
        {
            PostAppsToHome();
        }
    }

    /// <summary>Pede ao aplicativo que feche; se em cinco segundos ele continuar aberto, é encerrado.</summary>
    private static void CloseBlockedApp(Process process)
    {
        try
        {
            process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                // Já saiu, ou não deu para encerrar: fica como está e a próxima configuração tenta de novo.
            }
        });
    }

    private static void BringToFront(Process process)
    {
        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                OtherWindows.BringToFront(handle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // O processo acabou de sair; o evento Exited cuida do resto.
        }
    }

    private static void RequestAppClose(RunningApp run)
    {
        try
        {
            run.Process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Já saiu.
        }
    }
}
