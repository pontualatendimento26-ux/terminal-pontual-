using System.Windows;
using Microsoft.Web.WebView2.Core;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Terminal Pontual, versão 1.0 de teste. Opções de linha de comando:
/// <c>--janela</c> abre em janela normal (desenvolvimento); <c>--reconfigurar</c> volta a mostrar a
/// escolha entre "sem servidor" e "conectar a um servidor".
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private EnrollmentStore? _store;
    private bool _windowed;
    private bool _locked;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool Has(string option) => e.Args.Any(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));

        // Modo bloqueado (versão 2.0): o Windows abre este programa no lugar da área de trabalho.
        // O primeiro processo só vigia: se o Terminal fechar ou travar, ele abre de novo, para a tela nunca ficar vazia.
        _locked = Has("--bloqueado");
        if (_locked && !Has("--sessao"))
        {
            RunGuardian(Has("--janela"));
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Pontual.Terminal.Shell", out var first);
        if (!first)
        {
            // Já existe um Terminal aberto nesta sessão do Windows.
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // Um erro inesperado numa tela não pode fechar o terminal da atendente: vai para o diário e o programa segue.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("interface", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("tarefa", args.Exception);
            args.SetObserved();
        };

        _windowed = Has("--janela");

        if (!IsWebView2Available(out var problem))
        {
            MessageBox.Show(problem, "Terminal Pontual", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _store = new EnrollmentStore(EnrollmentStore.DefaultDirectory, new DpapiProtector());
        var enrollment = _store.Load();
        if (enrollment is null || Has("--reconfigurar"))
        {
            ShowEnrollment(enrollment?.ServerUrl);
        }
        else
        {
            ShowMain(enrollment);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>Vigia do modo bloqueado: mantém sempre um Terminal aberto. Só termina quando a sessão do Windows termina.</summary>
    private static void RunGuardian(bool windowed)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var arguments = "--bloqueado --sessao" + (windowed ? " --janela" : "");
        while (true)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = false });
                process?.WaitForExit();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error("vigia", ex);
            }

            // Se o Terminal caiu logo ao abrir, espera um pouco para não ficar piscando.
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }
    }

    /// <summary>O Windows 11 já traz o WebView2 Runtime; no Windows 10 ele pode faltar.</summary>
    private static bool IsWebView2Available(out string problem)
    {
        problem = "";
        try
        {
            if (!string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException or System.IO.IOException)
        {
            Log.Error("WebView2", ex);
        }

        problem =
            "O Terminal Pontual não pôde abrir porque falta um componente do Windows neste computador: " +
            "o \"Microsoft Edge WebView2 Runtime\".\n\n" +
            "Como resolver: peça ao TI para instalar o WebView2 Runtime (instalador \"Evergreen\", versão x64), " +
            "disponível em https://developer.microsoft.com/microsoft-edge/webview2/ , e abra o Terminal de novo.";
        return false;
    }

    private void ShowEnrollment(string? serverUrl)
    {
        var window = new EnrollWindow(_store!, serverUrl);
        var done = false;
        window.Completed += enrollment =>
        {
            done = true;
            ShowMain(enrollment);
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            if (!done)
            {
                Shutdown();
            }
        };
        MainWindow = window;
        window.Show();
    }

    private void ShowMain(Enrollment enrollment)
    {
        var main = new MainWindow(enrollment, _windowed, _locked);
        var reenrolling = false;
        main.Unenrolled += (_, _) =>
        {
            // O servidor respondeu 401: a máquina foi removida. Apaga o cadastro e volta à primeira tela.
            reenrolling = true;
            _store!.Delete();
            ShowEnrollment(enrollment.ServerUrl);
            main.ForceClose();
        };
        main.Closed += (_, _) =>
        {
            if (!reenrolling)
            {
                Shutdown();
            }
        };
        MainWindow = main;
        main.Show();
    }
}
