using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Janela principal do Terminal: a tela inicial da atendente num WebView2 e um WebView2 por site aberto,
/// sob uma barra fina própria. Segue docs/V1-CONTRATO.md (seções 5 e 6).
/// Dois modos: com servidor (heartbeat, configuração e eventos pela API) e local (lista de
/// config-local.json, eventos em registro.log, nenhuma chamada de rede do programa).
/// Tudo aqui roda na linha de execução da interface; as esperas de rede são assíncronas.
/// </summary>
public partial class MainWindow : Window
{
    private const string LocalHostName = "terminal.pontual.local";
    private const int ExitHotkeyId = 0x504F;
    private const int LeaveTestHotkeyId = 0x5050;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LocalCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Enrollment _enrollment;
    private readonly bool _windowed;
    private readonly bool _local;
    private readonly bool _locked;
    private readonly ServerApi? _api;
    private readonly EventQueue _queue = new();
    private readonly LocalEventLog _localLog = new(Path.Combine(EnrollmentStore.DefaultDirectory, LocalEventLog.FileName));
    private readonly string _localConfigPath = Path.Combine(AppContext.BaseDirectory, LocalConfigFile.FileName);
    private readonly ObservableCollection<SiteTab> _tabs = [];
    private readonly Dictionary<string, RunningProgram> _programs = [];
    private readonly List<MonitorWindow> _monitors = [];
    private readonly ObservableCollection<OpenWindow> _windows = [];
    private readonly DispatcherTimer _windowsTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTimeOffset _sessionStartedAt = DateTimeOffset.UtcNow;
    private readonly WebView2 _home = new();
    private readonly string _homeUrl;
    private readonly string _homeOrigin;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private CoreWebView2Environment? _environment;
    private string? _injectedScriptId;
    private TerminalConfig? _config;
    /// <summary>Cofre de senhas do navegador interno ligado nesta máquina (chave por computador, Terminal dedicado).</summary>
    private volatile bool _savePasswords;
    private HostPolicy _policy = HostPolicy.Empty;
    private bool _observationMode;
    private string _state = HeartbeatResponse.StateActive;
    private MaintenanceInfo? _maintenance;
    private string _attendantName = "";
    private bool? _online;
    private bool _homeReady;
    private bool _homeFailed;
    private string? _fatalError;
    private string? _localConfigError;
    private DateTime? _localConfigStamp;
    private SiteTab? _active;
    private bool _allowClose;
    private bool _confirmingExit;
    private bool _flushing;
    private bool _unenrolled;
    private HwndSource? _hwndSource;

    /// <summary>Modo bloqueado: a barra numa janela "sempre visível", por cima de qualquer programa. Nula na janela comum.</summary>
    private BarWindow? _barWindow;

    internal MainWindow(Enrollment enrollment, bool windowed, bool locked = false)
    {
        _enrollment = enrollment;
        _windowed = windowed;
        _locked = locked;
        _local = enrollment.Mode == TerminalMode.Local;
        if (_local)
        {
            _homeOrigin = $"https://{LocalHostName}";
        }
        else
        {
            _homeOrigin = enrollment.ServerUrl.TrimEnd('/');
            _api = new ServerApi(enrollment.ServerUrl, enrollment.Token);
        }

        _homeUrl = _homeOrigin + (_local ? "/index.html" : "/terminal/");

        InitializeComponent();
        if (locked && !windowed)
        {
            // A barra sai da janela e vai para a janela própria; fica um espaço vazio do mesmo tamanho embaixo.
            Root.Children.Remove(Bar);
            var space = new System.Windows.Controls.Border { Height = BarWindow.BarHeight, Background = System.Windows.Media.Brushes.White };
            Grid.SetRow(space, 1);
            Root.Children.Add(space);
            _barWindow = new BarWindow(Bar);
        }

        WireBar(Bar, monitor: null);
        _windowsTimer.Tick += (_, _) =>
        {
            RefreshWindows();
            KeepBarsOnTop();
        };

        if (!windowed)
        {
            // Tela cheia, sem borda e sem botões de fechar.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }

        _clock.Tick += (_, _) =>
        {
            UpdateClock();
            RestIdleTabs();
        };
        _toastTimer.Tick += (_, _) => HideToast();
        Loaded += OnLoaded;
        Deactivated += (_, _) => HideToast();
        UpdateClock();
        UpdateAttendant();
    }

    /// <summary>O servidor recusou o token (401): a máquina foi removida e precisa de novo cadastro.</summary>
    internal event EventHandler? Unenrolled;

    /// <summary>Fecha de verdade (sem passar pela trava do Alt+F4).</summary>
    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    // ------------------------------------------------------------------ janela e saída

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // SAÍDA SÓ PARA O TESTE: Ctrl+Shift+Alt+Q pede confirmação e fecha o Terminal.
        // Na versão de produção a saída será controlada pelo TI (bloqueio do Windows); esta combinação deve sair.
        // É um atalho registrado no Windows porque, com o foco dentro de um site, a janela WPF não recebe as teclas.
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (_hwndSource is not null)
        {
            _hwndSource.AddHook(WndProc);
            var modifiers = NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
            if (!NativeMethods.RegisterHotKey(_hwndSource.Handle, ExitHotkeyId, modifiers, NativeMethods.VkQ))
            {
                Log.Info("Não foi possível registrar o atalho de saída; ele só funciona com o foco fora dos sites.");
            }

            // SAÍDA DO TESTE 2.0: Ctrl+Alt+Espaço+P sai da conta bloqueada e volta à tela de entrada do Windows.
            // O Windows só registra atalhos com uma tecla comum; por isso o registrado é Ctrl+Alt+P e o Espaço é conferido na hora.
            if (_locked && !NativeMethods.RegisterHotKey(_hwndSource.Handle, LeaveTestHotkeyId,
                    NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, NativeMethods.VkP))
            {
                Log.Info("Não foi possível registrar o atalho Ctrl+Alt+Espaço+P.");
            }
        }

        StartShortcuts();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Q && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
        {
            e.Handled = true;
            ConfirmExit();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 (ou qualquer outro pedido de fechar) não fecha o Terminal em tela cheia.
        if (!_allowClose && !_windowed)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _clock.Stop();
        _toastTimer.Stop();
        Toast.IsOpen = false;
        CloseAssistant();
        CloseCalculator();
        ClosePdfTools();
        CloseNotes();
        StopShortcuts();
        if (_hwndSource is not null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, ExitHotkeyId);
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, LeaveTestHotkeyId);
            _hwndSource.RemoveHook(WndProc);
        }

        foreach (var tab in _tabs)
        {
            tab.IsClosed = true;
            tab.View.Dispose();
        }

        _windowsTimer.Stop();
        StopStoreShell();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        ReleaseBarSpace();
        foreach (var monitor in _monitors)
        {
            monitor.ForceClose();
        }

        _monitors.Clear();
        _barWindow?.ForceClose();
        _home.Dispose();
        _api?.Dispose();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmGetMinMaxInfo && _locked && !_windowed)
        {
            FillMonitorWhenMaximized(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == ExitHotkeyId)
        {
            handled = true;
            // O atalho é global; só vale quando o Terminal é a janela em uso.
            if (IsActive)
            {
                ConfirmExit();
            }
        }

        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == LeaveTestHotkeyId)
        {
            handled = true;
            // Vale mesmo com outra janela na frente (o explorador de arquivos, por exemplo), desde que o Espaço esteja apertado.
            if (NativeMethods.GetAsyncKeyState(NativeMethods.VkSpace) < 0)
            {
                Dispatcher.BeginInvoke(ConfirmLeaveTest);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Atalho do TI (Ctrl+Alt+Espaço+P): REMOVE o bloqueio desta conta e sai para a tela de entrada do Windows.
    /// Ao entrar de novo, a conta abre a área de trabalho normal (sem o Terminal). É a saída de emergência
    /// que impede ficar preso no Terminal mesmo quando esta é a única conta de administrador do computador.
    /// </summary>
    private void ConfirmLeaveTest()
    {
        if (_confirmingExit)
        {
            return;
        }

        _confirmingExit = true;
        try
        {
            HideToast();
            Activate();
            var answer = MessageBox.Show(this,
                "Liberar esta conta e sair do Terminal?\n\n" +
                "O bloqueio será REMOVIDO desta conta. Vai aparecer uma janela do Windows pedindo permissão de " +
                "administrador — clique Sim (ou informe uma conta de administrador). Em seguida o computador volta para " +
                "a tela de entrada e, ao entrar de novo nesta conta, ela abre a área de trabalho normal (sem o Terminal).",
                "Terminal Pontual", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            // As travas do bloqueio ficam em chaves de política, que só um administrador apaga. Por isso o Terminal
            // se relança com poderes de administrador (--desbloquear): esse processo remove o bloqueio e sai da conta.
            if (!Unblock.StartElevatedRemoval())
            {
                MessageBox.Show(this,
                    "A liberação foi cancelada (a permissão de administrador não foi concedida). A conta continua no Terminal.",
                    "Terminal Pontual", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _confirmingExit = false;
        }
    }

    /// <summary>Modo bloqueado: desligar, reiniciar ou sair da conta do Windows.</summary>
    private void ShowPowerMenu(Window owner)
    {
        if (_confirmingExit)
        {
            return;
        }

        _confirmingExit = true;
        try
        {
            HideToast();
            PowerWindow.ShowFor(owner);
        }
        finally
        {
            _confirmingExit = false;
        }
    }

    /// <summary>
    /// Modo bloqueado: abre o explorador de arquivos do Windows na pasta Downloads (ou Documentos, se ela não existir).
    /// O que ela consegue abrir a partir dele é limitado pelas políticas da conta (só programas liberados).
    /// </summary>
    private void OpenFiles()
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
            var folder = DownloadsFolder();
            if (!Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            if (Directory.Exists(folder))
            {
                info.ArgumentList.Add(folder);
            }

            Process.Start(info)?.Dispose();
            Record(DeviceEvent.Opened, "Explorador de arquivos");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("abrir arquivos", ex);
            ShowToast("Não foi possível abrir os arquivos. Chame o TI.");
        }
    }

    private void ConfirmExit()
    {
        if (_locked)
        {
            // No modo bloqueado não existe "fechar o Terminal": o atalho abre o menu de desligar.
            ShowPowerMenu(this);
            return;
        }

        if (_confirmingExit)
        {
            return;
        }

        _confirmingExit = true;
        try
        {
            HideToast();
            var answer = MessageBox.Show(this, "Fechar o Terminal Pontual?\n\nAs páginas abertas serão fechadas.", "Terminal Pontual",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                ForceClose();
            }
        }
        finally
        {
            _confirmingExit = false;
        }
    }

    // ------------------------------------------------------------------ partida

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _clock.Start();
            _barWindow?.Show();
            SyncMonitors();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            if (_locked)
            {
                // No modo bloqueado não há barra de tarefas do Windows: a nossa mostra também as janelas dos outros programas.
                RefreshWindows();
                _windowsTimer.Start();
            }

            await InitializeAsync();
        }
        catch (Exception ex)
        {
            // Manipulador "async void": nada pode escapar daqui.
            Log.Error("partida", ex);
            _fatalError = ex is WebView2RuntimeNotFoundException
                ? "Falta o componente \"Microsoft Edge WebView2 Runtime\" neste computador. Peça ao TI para instalar e abra o Terminal de novo."
                : "O Terminal não conseguiu preparar o navegador interno. Chame o TI.";
            UpdateLayoutState();
        }
    }

    private async Task InitializeAsync()
    {
        var dataFolder = Path.Combine(EnrollmentStore.DefaultDirectory, "WebView2");
        Directory.CreateDirectory(dataFolder);
        var options = new CoreWebView2EnvironmentOptions { Language = "pt-BR" };
        _environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: dataFolder, options);

        ContentHost.Children.Add(_home);
        await _home.EnsureCoreWebView2Async(_environment);
        var core = _home.CoreWebView2;
        ApplySettings(core, isHome: true);

        if (_local)
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "terminal");
            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                _fatalError = $"Faltam os arquivos da tela inicial na pasta {folder}. Copie de novo a pasta do programa.";
                UpdateLayoutState();
                return;
            }

            // Tela inicial embutida: a pasta "terminal" ao lado do programa responde por um domínio só dele.
            core.SetVirtualHostNameToFolderMapping(LocalHostName, folder, CoreWebView2HostResourceAccessKind.Deny);
            CheckLocalConfig();
        }

        await InjectBridgeAsync();
        core.WebMessageReceived += OnHomeMessage;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            // A tela inicial nunca sai do próprio endereço.
            if (!IsHomeUrl(args.Uri))
            {
                args.Cancel = true;
            }
        };
        core.NavigationCompleted += (_, args) =>
        {
            _homeFailed = !args.IsSuccess || args.HttpStatusCode >= 400;
            if (_homeFailed)
            {
                _homeReady = false;
            }

            UpdateLayoutState();
        };
        core.Navigate(_homeUrl);

        Record(DeviceEvent.SessionStarted);
        _ = RunLoopAsync(_cts.Token);
        _ = ChromeLoopAsync(_cts.Token);
        if (!_local)
        {
            _ = ScreenLoopAsync(_cts.Token);
            _ = ControlLoopAsync(_cts.Token);
            StartRecording(_cts.Token);
        }
    }

    /// <summary>Injeta <c>window.PONTUAL</c> antes de qualquer script da tela inicial (seção 6 do contrato).</summary>
    private async Task InjectBridgeAsync()
    {
        var core = _home.CoreWebView2;
        if (_injectedScriptId is not null)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(_injectedScriptId);
        }

        object data = _local
            ? new { hosted = true, local = true, locked = _locked, hostname = Environment.MachineName, config = _config, calc = true, pdf = true, notes = true }
            : new { token = _enrollment.Token, hostname = Environment.MachineName, hosted = true, locked = _locked, apps = true, calc = true, pdf = true, notes = true };

        // Só a página do próprio Terminal, e só o quadro principal, recebe o objeto (ele carrega o token).
        // O serializador escapa <, >, & e aspas, então o JSON pode ir direto para o script.
        var script =
            "(function(){if(window.top!==window||location.origin!==" + JsonSerializer.Serialize(_homeOrigin) + ")return;" +
            "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + JsonSerializer.Serialize(data, Json) + "),writable:false,configurable:false});})();";
        _injectedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private bool IsHomeUrl(string? url) =>
        url is not null
        && (url.Equals(_homeOrigin, StringComparison.OrdinalIgnoreCase) || url.StartsWith(_homeOrigin + "/", StringComparison.OrdinalIgnoreCase))
        && (_local || url.StartsWith(_homeOrigin + "/terminal/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Trava comum a todos os WebView2: sem DevTools, menu de contexto, atalhos do navegador,
    /// gerenciador de senhas, preenchimento automático nem downloads.
    /// </summary>
    private void ApplySettings(CoreWebView2 core, bool isHome, bool openPdfs = false)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPasswordAutosaveEnabled = _savePasswords && !isHome;
        settings.IsGeneralAutofillEnabled = _savePasswords && !isHome;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsWebMessageEnabled = isHome;

        core.DownloadStarting += (_, args) => OnDownloadStarting(args, openPdfs);

        // Endereços como mailto:, tel: ou ms-settings: abririam outros programas do Windows.
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;

        core.ProcessFailed += (_, args) =>
        {
            Log.Info($"Processo do WebView2 falhou: {args.ProcessFailedKind}");
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _fatalError = "O navegador interno parou de funcionar. Chame o TI para reiniciar o Terminal.";
                UpdateLayoutState();
            }
            else if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                try
                {
                    core.Reload();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    Log.Error("recarregar", ex);
                }
            }
        };
    }

    // ------------------------------------------------------------------ visualização ao vivo e controle pelo TI

    private static readonly TimeSpan ScreenAskInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ScreenFrameInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>O TI está com a tela aberta agora? O laço de controle só busca comandos enquanto isto é verdade.</summary>
    private volatile bool _screenWanted;

    /// <summary>A plaquinha "TI controlando", enquanto o controle está ativo. Vive só na thread da interface.</summary>
    private ControlNotice? _controlNotice;

    /// <summary>
    /// A cada poucos segundos pergunta ao servidor se o TI abriu a visualização desta tela. Enquanto estiver aberta,
    /// envia uma imagem por vez e mostra o aviso na barra; quando o TI fecha, para na hora. Nada é gravado aqui.
    /// </summary>
    private async Task ScreenLoopAsync(CancellationToken ct)
    {
        var api = _api!;
        while (!ct.IsCancellationRequested)
        {
            var wait = ScreenAskInterval;
            try
            {
                if (_state != HeartbeatResponse.StatePending && _online != false)
                {
                    var request = await api.GetScreenRequestAsync(ct);
                    _screenWanted = request.Wanted;
                    if (request.Wanted)
                    {
                        // O aviso fixo "As atividades serão monitoradas" já está sempre na barra; nada muda na tela agora.
                        var wanted = true;
                        while (wanted && !ct.IsCancellationRequested)
                        {
                            var frame = await Task.Run(() => ScreenCapture.Jpeg(maxWidth: 3200, maxBytes: 1_900_000), ct);
                            wanted = await api.SendScreenAsync(frame, ct, controlDebug: RemoteControl.DebugLine());
                            _screenWanted = wanted;
                            if (wanted)
                            {
                                await Task.Delay(ScreenFrameInterval, ct);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ServerApiException ex)
            {
                // Servidor de versão anterior (sem esta rota) ou máquina removida: tenta de novo mais tarde.
                if (ex.Status == System.Net.HttpStatusCode.NotFound)
                {
                    wait = TimeSpan.FromMinutes(1);
                }
            }
            catch (Exception ex) when (ServerApi.IsNetworkFailure(ex))
            {
                // Sem conexão: o ciclo principal já mostra isso.
            }
            catch (Exception ex)
            {
                Log.Error("tela", ex);
                wait = TimeSpan.FromSeconds(30);
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>Arquivos que executam alguma coisa ao serem abertos: esses nunca são baixados.</summary>
    private static readonly HashSet<string> BlockedDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".jar", ".lnk", ".reg", ".dll", ".cpl", ".msc", ".iso", ".img", ".url",
    };

    /// <summary>
    /// Os downloads dos sites vão direto para a pasta Downloads da atendente, sem a janela de downloads
    /// do navegador (que tem atalhos para abrir programas). Programas e scripts são recusados.
    /// </summary>
    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs args, bool openPdfs)
    {
        args.Handled = true;
        try
        {
            var name = Path.GetFileName(args.ResultFilePath);
            if (!string.IsNullOrWhiteSpace(name) && Path.GetExtension(name).Length == 0
                && args.DownloadOperation.MimeType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                name += ".pdf"; // PDF sem extensão no nome: sem ela o Windows não saberia abrir
            }

            if (string.IsNullOrWhiteSpace(name) || BlockedDownloadExtensions.Contains(Path.GetExtension(name)))
            {
                args.Cancel = true;
                ShowToast("Este tipo de arquivo não pode ser baixado neste computador. Se precisar dele, chame o TI.");
                return;
            }

            var folder = DownloadsFolder();
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);
            for (var copy = 1; File.Exists(target); copy++)
            {
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({copy}){Path.GetExtension(name)}");
            }

            args.ResultFilePath = target;
            var shown = Path.GetFileName(target);
            var operation = args.DownloadOperation;
            operation.StateChanged += (_, _) =>
            {
                if (operation.State == CoreWebView2DownloadState.Completed)
                {
                    if (openPdfs && IsPdf(target) && !_pdfsOnScreen.Remove(name))
                    {
                        // Fora do evento do WebView2, que não aceita criar outro WebView2 dentro dele.
                        Dispatcher.BeginInvoke(() => { _ = OpenPdfAsync(target); });
                        ShowToast($"Arquivo salvo na pasta Downloads: {shown}.");
                        return;
                    }

                    ShowToast($"Arquivo salvo na pasta Downloads: {shown}." + (_locked ? " Para abrir, use o botão Arquivos." : ""));
                }
                else if (operation.State == CoreWebView2DownloadState.Interrupted)
                {
                    Log.Info($"Download interrompido: {operation.InterruptReason}");
                    ShowToast($"O download de {shown} não foi concluído. Tente de novo.");
                }
            };
            ShowToast($"Baixando {shown}...");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            args.Cancel = true;
            Log.Error("download", ex);
            ShowToast("Não foi possível salvar o arquivo. Chame o TI.");
        }
    }

    private static string DownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // ------------------------------------------------------------------ ciclo: servidor ou arquivo local

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_local)
                {
                    CheckLocalConfig();
                }
                else
                {
                    await ServerTickAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nenhuma falha de rede ou de arquivo derruba o programa: fica no diário e o ciclo continua.
                Log.Error("ciclo", ex);
            }

            try
            {
                await Task.Delay(_local ? LocalCheckInterval : HeartbeatInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ServerTickAsync(CancellationToken ct)
    {
        var api = _api!;
        try
        {
            var heartbeat = await api.HeartbeatAsync(AppsInUse(), _sessionStartedAt, ct, Short(CurrentVersion), _coverageStatus);
            _online = true;
            _state = heartbeat.State;
            _maintenance = heartbeat.Maintenance;
            _observationMode = heartbeat.ObservationMode;
            _assistantEnabled = heartbeat.Assistant;
            UpdateWhatsAppLearning(heartbeat.WhatsAppLearning);
            UpdateCoverage(heartbeat.Coverage ?? "", heartbeat.CoverageNow);
            _ = CheckForUpdateAsync();
            if (!string.IsNullOrWhiteSpace(heartbeat.AttendantName))
            {
                _attendantName = heartbeat.AttendantName;
            }

            if (_state == HeartbeatResponse.StatePending)
            {
                // Máquina ainda não aprovada (ou que voltou a ficar pendente): nada fica aberto.
                ApplyConfig(null);
            }
            else if (_config is null || heartbeat.ConfigVersion != _config.Version)
            {
                ApplyConfig(await api.GetConfigAsync(ct));
                PostToHome(new { type = "config" });
            }
        }
        catch (ServerApiException ex) when (ex.IsUnauthorized)
        {
            Unenroll();
            return;
        }
        catch (ServerApiException ex)
        {
            // O servidor respondeu, mas com erro: há conexão; tenta de novo no próximo ciclo.
            _online = true;
            Log.Error("servidor", ex);
        }
        catch (Exception ex) when (ServerApi.IsNetworkFailure(ex) && !ct.IsCancellationRequested)
        {
            _online = false;
        }

        if (_homeFailed && _online == true)
        {
            _home.CoreWebView2?.Navigate(_homeUrl);
        }

        UpdateAttendant();
        SendStatusToHome();
        UpdateLayoutState();
        await FlushEventsAsync();
    }

    private void Unenroll()
    {
        if (_unenrolled)
        {
            return;
        }

        _unenrolled = true;
        _cts.Cancel();
        Unenrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Modo local: relê <c>config-local.json</c> quando o arquivo muda (o TI pode editá-lo com o Terminal aberto).</summary>
    private void CheckLocalConfig()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(_localConfigPath) ? File.GetLastWriteTimeUtc(_localConfigPath) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = DateTime.MinValue;
        }

        if (_localConfigStamp == stamp)
        {
            return;
        }

        var first = _localConfigStamp is null;
        _localConfigStamp = stamp;
        var result = LocalConfigFile.Load(_localConfigPath, Environment.UserName);
        if (result.Config is { } config)
        {
            _localConfigError = null;
            _observationMode = config.ObservationMode;
            _attendantName = config.AttendantName ?? "";
            ApplyConfig(config);
            UpdateAttendant();
            if (!first)
            {
                PostToHome(new { type = "config", config });
                _ = ReinjectAsync();
                ShowToast("A lista de sites foi atualizada.");
            }
        }
        else
        {
            _localConfigError = result.Error;
            Log.Info($"config-local.json: {result.Error}");
            if (_config is not null)
            {
                ShowToast($"O arquivo config-local.json tem um erro e a lista anterior foi mantida. {result.Error}");
            }
        }

        UpdateLayoutState();
    }

    private async Task ReinjectAsync()
    {
        try
        {
            await InjectBridgeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("reinjetar", ex);
        }
    }

    /// <summary>Troca a configuração em uso e fecha o que deixou de ser permitido.</summary>
    private void ApplyConfig(TerminalConfig? config)
    {
        _config = config;
        _policy = config is null ? HostPolicy.Empty : HostPolicy.FromConfig(config);
        ApplyChromeConfig(config);
        ApplyAppsConfig(config);
        ApplyLightshot();
        ApplyStoreShell(config);
        ApplyLightMode(config);
        _savePasswords = config?.SavePasswords ?? false;
        foreach (var tab in _tabs)
        {
            if (tab.View.CoreWebView2 is { } vaultCore)
            {
                vaultCore.Settings.IsPasswordAutosaveEnabled = _savePasswords;
                vaultCore.Settings.IsGeneralAutofillEnabled = _savePasswords;
            }
        }
        if (!_local && config is not null && !string.IsNullOrWhiteSpace(config.AttendantName))
        {
            _attendantName = config.AttendantName;
        }

        foreach (var tab in _tabs.Where(t => config?.Find(t.Id) is null).ToArray())
        {
            CloseTab(tab);
        }

        foreach (var program in _programs.Values.Where(p => config?.Find(p.Item.Id) is not { IsProgram: true }).ToArray())
        {
            RequestProgramClose(program);
        }
    }

    private string[] AppsInUse() => [.. _tabs.Select(t => t.Name), .. _programs.Values.Select(p => p.Item.Name), .. _apps.Values.Select(a => a.App.Name)];

    // ------------------------------------------------------------------ eventos

    private void Record(string type, string? item = null, string? detail = null)
    {
        var deviceEvent = new DeviceEvent(type, item, detail, DateTimeOffset.UtcNow);
        if (_local)
        {
            _localLog.Append(deviceEvent);
            return;
        }

        _queue.Enqueue(deviceEvent);
        _ = FlushEventsAsync();
    }

    /// <summary>Entrega a fila ao servidor. Se ele não responde, os eventos ficam na fila para o próximo ciclo. Nunca lança.</summary>
    private async Task FlushEventsAsync()
    {
        if (_flushing || _api is null || _unenrolled)
        {
            return;
        }

        _flushing = true;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var batch = _queue.Peek(50);
                if (batch.Count == 0)
                {
                    break;
                }

                try
                {
                    await _api.SendEventsAsync(batch, _cts.Token);
                    _queue.Acknowledge(batch);
                }
                catch (ServerApiException ex) when (!ex.IsUnauthorized)
                {
                    // O servidor recusou o lote de vez (400, por exemplo): repetir não adianta.
                    Log.Error("eventos recusados", ex);
                    _queue.Acknowledge(batch);
                }
            }
        }
        catch (Exception ex) when (ex is ServerApiException || ServerApi.IsNetworkFailure(ex) || ex is ObjectDisposedException)
        {
            // Sem servidor (ou token recusado, que o próximo heartbeat trata): a fila fica como está.
        }
        catch (Exception ex)
        {
            Log.Error("eventos", ex);
        }
        finally
        {
            _flushing = false;
        }
    }

    // ------------------------------------------------------------------ ponte com a tela inicial

    private void OnHomeMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsHomeUrl(e.Source))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            switch (typeElement.GetString())
            {
                case "ready":
                    _homeReady = true;
                    SendStateToHome();
                    SendStatusToHome();
                    break;
                case "open" when id is not null:
                    OpenItem(id);
                    break;
                case "focus" when id is not null:
                    FocusItem(id);
                    break;
                case "close" when id is not null:
                    CloseItem(id);
                    break;
                case "help":
                    Record(DeviceEvent.Help);
                    break;
                case "calc":
                    // Calculadora numa janela própria (MainWindow.Calculator.cs): solta, redimensionável e visível em qualquer aba.
                    _ = ShowCalculatorAsync();
                    break;
                case "pdf":
                    // Ferramentas de PDF (ler, compactar, converter) numa janela própria (MainWindow.PdfTools.cs).
                    _ = ShowPdfToolsAsync();
                    break;
                case "apps" when !_local:
                    // Lista "Aplicativos": a varredura do computador roda fora da linha da interface.
                    _ = SendAppsToHomeAsync();
                    break;
                case "open-app" when id is not null && !_local:
                    OpenApp(id);
                    break;
                case "files" when _locked:
                    OpenFiles();
                    break;
                case "power" when _locked:
                    // Fora do tratamento da mensagem: a janela de desligar é modal.
                    Dispatcher.BeginInvoke(() => ShowPowerMenu(this));
                    break;
                default:
                    // Notas adesivas (MainWindow.Notes.cs): abrir a lista, mostrar ou fechar a janela de uma nota.
                    HandleNoteMessage(root, typeElement.GetString(), null);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("mensagem da tela inicial", ex);
        }
    }

    private void PostToHome(object message)
    {
        if (!_homeReady || _home.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("mensagem para a tela inicial", ex);
        }
    }

    /// <summary>Mensagem <c>state</c> do contrato: o que está aberto agora (abas e programas).</summary>
    private void SendStateToHome()
    {
        var open = _tabs.Select(t => new { id = t.Id, name = t.Name })
            .Concat(_programs.Values.Select(p => new { id = p.Item.Id, name = p.Item.Name }))
            .Concat(_apps.Values.Select(a => new { id = AppId(a.App), name = a.App.Name }))
            .ToArray();
        PostToHome(new { type = "state", open });
    }

    /// <summary>
    /// Mensagem <c>status</c> (acréscimo ao contrato): como é o programa que envia o heartbeat, é ele quem
    /// conta à tela inicial o estado da máquina (pendente, manutenção) e se há conexão.
    /// </summary>
    private void SendStatusToHome()
    {
        if (_local || _online is null)
        {
            return;
        }

        PostToHome(new
        {
            type = "status",
            state = _state,
            online = _online.Value,
            attendantName = _attendantName,
            maintenance = _maintenance is null ? null : new { reason = _maintenance.Reason, until = _maintenance.Until },
        });
    }

    // ------------------------------------------------------------------ abrir, focar e fechar

    private void OpenItem(string id)
    {
        if (_config?.Find(id) is not { } item)
        {
            return;
        }

        if (item.IsProgram)
        {
            StartProgram(item);
            return;
        }

        if (_tabs.FirstOrDefault(t => t.Id == id) is { } existing)
        {
            // Pedido feito na tela inicial, que fica no monitor principal: a página vem para ele.
            ShowTab(existing, monitor: null);
            return;
        }

        // O próprio endereço do item passa pela mesma política das navegações.
        if (_policy.Evaluate(item.Target).Verdict == NavigationVerdict.SchemeBlocked)
        {
            ShowToast($"O endereço de {item.Name} está incorreto. Chame o TI.");
            return;
        }

        _ = OpenSiteAsync(item);
    }

    private async Task OpenSiteAsync(ConfigItem item)
    {
        if (_environment is null)
        {
            return;
        }

        var tab = new SiteTab(item, new WebView2());
        try
        {
            _tabs.Add(tab);
            ContentHost.Children.Add(tab.View);
            Activate(tab);
            Record(DeviceEvent.Opened, item.Name);
            SendStateToHome();

            await tab.View.EnsureCoreWebView2Async(_environment);
            if (tab.IsClosed)
            {
                return;
            }

            var core = tab.View.CoreWebView2;
            ApplySettings(core, isHome: false, openPdfs: IsNexus(tab));
            core.NavigationStarting += (_, args) =>
            {
                if (IsAllowedBlob(args.Uri))
                {
                    args.Cancel = true;
                    OpenBlobInWindow(tab, args.Uri);
                    return;
                }

                OnSiteNavigationStarting(tab, args);
            };
            core.NewWindowRequested += (_, args) => OnSiteNewWindow(tab, core, args);
            WireSiteKeys(tab.View);
            await AllowNexusDownloadsAsync(tab);
            if (tab.IsClosed)
            {
                return;
            }

            core.Navigate(item.Target);
        }
        catch (Exception ex)
        {
            Log.Error($"abrir {item.Name}", ex);
            if (!tab.IsClosed)
            {
                CloseTab(tab);
            }

            ShowToast($"Não foi possível abrir {item.Name}. Tente de novo ou chame o TI.");
        }
    }

    /// <summary>Política de navegação da página principal da aba (classe <see cref="HostPolicy"/>).</summary>
    private void OnSiteNavigationStarting(SiteTab tab, CoreWebView2NavigationStartingEventArgs args)
    {
        var decision = _policy.Evaluate(args.Uri);
        switch (decision.Verdict)
        {
            case NavigationVerdict.Allowed:
                return;

            case NavigationVerdict.SchemeBlocked:
                // file:, javascript:, data:, mailto: e afins nunca abrem.
                args.Cancel = true;
                return;

            case NavigationVerdict.NotListed when _observationMode:
                // Modo de observação: deixa passar e registra o domínio para o TI avaliar.
                var now = DateTime.UtcNow;
                if (tab.LastObservedHost != decision.Host || now - tab.LastObservedAt > TimeSpan.FromSeconds(10))
                {
                    Record(DeviceEvent.Observed, tab.Name, decision.Host);
                }

                tab.LastObservedHost = decision.Host;
                tab.LastObservedAt = now;
                return;

            default:
                args.Cancel = true;
                Record(DeviceEvent.Blocked, tab.Name, decision.Host);
                if (tab.Float is not null)
                {
                    // Em janela solta não há onde pôr o aviso de tela cheia: a página fica onde estava e sai um aviso rápido.
                    ShowToast($"Este site não é permitido: {decision.Host}. A tentativa foi registrada" + (_local ? "." : " e o TI foi avisado."));
                    return;
                }

                tab.BlockedHost = decision.Host;
                UpdateLayoutState();
                return;
        }
    }

    private void FocusItem(string id)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is { } tab)
        {
            ShowTab(tab, monitor: null);
        }
        else if (_programs.TryGetValue(id, out var program))
        {
            // Sem janela (navegador em segundo plano), abre de novo.
            StartProgram(program.Item);
        }
        else if (TryRunningApp(id, out var app))
        {
            if (!BringToFront(app.Process))
            {
                ReopenApp(app.App);
            }
        }
    }

    private void CloseItem(string id)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is { } tab)
        {
            CloseTab(tab);
        }
        else if (_programs.TryGetValue(id, out var program))
        {
            RequestProgramClose(program);
        }
        else if (TryRunningApp(id, out var app))
        {
            RequestAppClose(app);
        }
    }

    /// <summary>Com nulo, mostra a tela inicial no monitor principal; com uma aba, mostra-a no monitor em que ela já está.</summary>
    private void Activate(SiteTab? tab)
    {
        if (tab is not null)
        {
            ShowTab(tab, tab.Monitor);
            return;
        }

        _active = null;
        UpdateLayoutState();
        if (Overlay.Visibility != Visibility.Visible)
        {
            _home.Focus();
        }
    }

    /// <summary>Onde ficam os WebView2 de cada janela: a principal (nulo) ou a de um monitor secundário.</summary>
    private Panel HostOf(MonitorWindow? monitor) => monitor?.Host ?? ContentHost;

    /// <summary>
    /// Mostra a aba no monitor pedido (nulo = principal). Se ela estava em outro monitor, muda de lugar;
    /// o monitor de onde saiu volta à tela inicial (o principal) ou fica livre (os demais).
    /// </summary>
    private void ShowTab(SiteTab tab, MonitorWindow? monitor)
    {
        if (tab.IsClosed)
        {
            return;
        }

        if (tab.Float is { } floating)
        {
            // Em janela solta: ela vem para a frente (e reaparece, se estava minimizada).
            floating.BringFront();
            tab.View.Focus();
            return;
        }

        if (tab.Monitor != monitor)
        {
            HostOf(tab.Monitor).Children.Remove(tab.View);
            if (tab.Monitor is { } previous)
            {
                if (previous.Active == tab)
                {
                    previous.Active = null;
                }
            }
            else if (_active == tab)
            {
                _active = null;
            }

            tab.Monitor = monitor;
            HostOf(monitor).Children.Add(tab.View);
        }

        if (monitor is null)
        {
            _active = tab;
        }
        else
        {
            monitor.Active = tab;
        }

        UpdateLayoutState();
        if (Overlay.Visibility != Visibility.Visible && tab.BlockedHost is null)
        {
            tab.View.Focus();
        }
    }

    private void CloseTab(SiteTab tab)
    {
        if (tab.IsClosed)
        {
            return;
        }

        tab.IsClosed = true;
        _tabs.Remove(tab);
        if (tab.Float is { } floating)
        {
            tab.Float = null;
            floating.Release();
            floating.Close();
        }

        HostOf(tab.Monitor).Children.Remove(tab.View);
        if (tab.Monitor is { } monitor && monitor.Active == tab)
        {
            monitor.Active = null;
        }

        tab.View.Dispose();
        Record(DeviceEvent.Closed, tab.Name);
        SendStateToHome();
        if (_active == tab)
        {
            Activate(null);
        }
        else
        {
            UpdateLayoutState();
        }
    }

    // ------------------------------------------------------------------ monitores e barra

    private IEnumerable<TerminalBar> Bars => _monitors.Select(m => m.Bar).Prepend(Bar);

    /// <summary>Liga os botões de uma barra. Cada barra mostra as páginas no monitor em que ela está.</summary>
    private void WireBar(TerminalBar bar, MonitorWindow? monitor)
    {
        bar.Bind(_tabs, _windows);
        bar.SetLocked(_locked);
        // Com a barra em janela própria, clicar nela não traz mais o Terminal para a frente sozinho: é feito aqui.
        Window host = (Window?)monitor ?? this;
        void Front()
        {
            if (_barWindow is not null)
            {
                host.Activate();
            }
        }

        bar.HomeClick += () =>
        {
            Front();
            Activate(null);
        };
        bar.HelpClick += () =>
        {
            Front();
            CallHelp();
        };
        bar.FilesClick += OpenFiles;
        bar.PowerClick += () =>
        {
            Front();
            ShowPowerMenu(host);
        };
        bar.ShowUpdate(_locked && !_local);
        bar.UpdateClick += () =>
        {
            Front();
            StartUpdate(host);
        };
        bar.TabClick += tab =>
        {
            Front();
            ShowTab(tab, monitor);
        };
        bar.TabCloseClick += CloseTab;
        bar.TabFloatClick += tab =>
        {
            Front();
            ToggleFloat(tab);
        };
        bar.WindowClick += window => OtherWindows.BringToFront(window.Handle);

        // Pesquisar: só o que está liberado agora; com a tela de bloqueio (manutenção, erro) não lista nem abre nada.
        bar.SearchItems = () => CurrentOverlay() is null ? _config?.Items ?? [] : [];
        bar.SearchPick += item =>
        {
            if (CurrentOverlay() is not null || _config?.Find(item.Id) is null)
            {
                return;
            }

            Front();
            if (_tabs.FirstOrDefault(t => t.Id == item.Id) is { } open)
            {
                ShowTab(open, monitor);
                return;
            }

            OpenItem(item.Id);
        };
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(SyncMonitors);

    /// <summary>
    /// Mantém uma janela do Terminal em cada monitor além do principal. Chamado na partida e quando
    /// um monitor é ligado, desligado ou muda de resolução. Em janela comum (<c>--janela</c>) não se aplica.
    /// </summary>
    private void SyncMonitors()
    {
        if (_windowed || _allowClose)
        {
            return;
        }

        try
        {
            _barWindow?.Place(Monitors.Primary());
            var bounds = Monitors.Secondary();

            // Monitor que saiu: as páginas dele voltam para o principal.
            while (_monitors.Count > bounds.Count)
            {
                var gone = _monitors[^1];
                _monitors.RemoveAt(_monitors.Count - 1);
                foreach (var tab in _tabs.Where(t => t.Monitor == gone))
                {
                    gone.Host.Children.Remove(tab.View);
                    tab.Monitor = null;
                    ContentHost.Children.Add(tab.View);
                }

                gone.Active = null;
                gone.ForceClose();
            }

            for (var i = 0; i < bounds.Count; i++)
            {
                if (i < _monitors.Count)
                {
                    _monitors[i].Place(bounds[i]);
                    continue;
                }

                var monitor = new MonitorWindow(bounds[i], floatingBar: _barWindow is not null);
                WireBar(monitor.Bar, monitor);
                monitor.BlockedBack += ResolveBlocked;
                _monitors.Add(monitor);
                monitor.Show();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Error("monitores", ex);
        }

        UpdateClock();
        UpdateAttendant();
        UpdateLayoutState();

        // Depois de as barras serem medidas: tira a faixa delas da área dos programas maximizados.
        Dispatcher.BeginInvoke(ReserveBarSpace, DispatcherPriority.Loaded);
    }

    /// <summary>Outro programa "sempre visível" pode ter passado na frente das barras: elas voltam para o topo.</summary>
    private void KeepBarsOnTop()
    {
        _barWindow?.KeepOnTop();
        foreach (var monitor in _monitors)
        {
            monitor.FloatingBar?.KeepOnTop();
        }
    }

    /// <summary>Atualiza a lista de janelas dos outros programas mostrada na barra (só no modo bloqueado).</summary>
    private void RefreshWindows()
    {
        try
        {
            var current = OtherWindows.List();
            for (var i = _windows.Count - 1; i >= 0; i--)
            {
                if (!current.Any(c => c.Handle == _windows[i].Handle))
                {
                    _windows.RemoveAt(i);
                }
            }

            foreach (var (handle, title) in current)
            {
                if (_windows.FirstOrDefault(w => w.Handle == handle) is { } known)
                {
                    known.Title = title;
                }
                else
                {
                    _windows.Add(new OpenWindow(handle, title));
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("janelas", ex);
        }
    }

    // ------------------------------------------------------------------ programas instalados

    /// <summary>Abre o programa (ou traz para a frente). Devolve null se abriu, ou o motivo de não ter aberto.</summary>
    /// <param name="argument">Endereço passado ao navegador (pesquisa pedida pela assistente).</param>
    private string? StartProgram(ConfigItem item, string? argument = null)
    {
        // Já aberto e com janela: vem para a frente. Aberto SEM janela (o Edge e o Chrome continuam ligados em segundo
        // plano depois de a atendente fechar a janela): segue e abre de novo, senão o clique no ícone não faz nada.
        if (argument is null && _programs.TryGetValue(item.Id, out var running) && BringToFront(running))
        {
            return null;
        }

        // Confere de novo no cliente: só o caminho que veio na configuração, e nunca os recusados do contrato.
        if (!ProgramPolicy.CanStart(item.Target, _config))
        {
            Log.Info($"Programa recusado pelo terminal: {item.Target}");
            ShowToast($"{item.Name} não pode ser aberto neste computador. Chame o TI.");
            return $"{item.Name} não pode ser aberto neste computador. Chame o TI.";
        }

        if (!File.Exists(item.Target))
        {
            ShowToast($"{item.Name} não está instalado neste computador. Chame o TI.");
            return $"{item.Name} não está instalado neste computador. Chame o TI.";
        }

        Process? process;
        try
        {
            var start = new ProcessStartInfo(item.Target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(item.Target) ?? "",
            };
            if (argument is not null)
            {
                start.ArgumentList.Add(argument);
            }

            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"iniciar {item.Name}", ex);
            process = null;
        }

        if (process is null)
        {
            ShowToast($"Não foi possível abrir {item.Name}. Chame o TI.");
            return $"Não foi possível abrir {item.Name}. Chame o TI.";
        }

        if (_programs.ContainsKey(item.Id))
        {
            // Navegador já aberto: a pesquisa vira uma aba nova nele (ou ele abre uma janela nova, se estava sem
            // nenhuma), e o processo novo sai sozinho.
            process.Dispose();
            return null;
        }

        var program = new RunningProgram(item, process);
        _programs[item.Id] = program;
        Record(DeviceEvent.Opened, item.Name);
        SendStateToHome();

        // Conta como "em uso" enquanto o processo existir.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnProgramExited(program));
        if (process.HasExited)
        {
            OnProgramExited(program);
        }

        return null;
    }

    private void OnProgramExited(RunningProgram program)
    {
        if (!_programs.TryGetValue(program.Item.Id, out var current) || !ReferenceEquals(current, program))
        {
            return;
        }

        _programs.Remove(program.Item.Id);
        program.Process.Dispose();
        if (!_cts.IsCancellationRequested)
        {
            Record(DeviceEvent.Closed, program.Item.Name);
            SendStateToHome();
        }
    }

    /// <summary>Traz a janela do programa para a frente. Devolve false se ele não tem janela para mostrar.</summary>
    private static bool BringToFront(RunningProgram program)
    {
        try
        {
            program.Process.Refresh();
            var handle = program.Process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            if (NativeMethods.IsIconic(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            }

            NativeMethods.SetForegroundWindow(handle);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // O processo acabou de sair; o evento Exited cuida do resto.
            return false;
        }
    }

    /// <summary>Pede ao programa que feche (como o X da janela dele); ele ainda pode perguntar se salva o trabalho.</summary>
    private static void RequestProgramClose(RunningProgram program)
    {
        try
        {
            program.Process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Já saiu.
        }
    }

    // ------------------------------------------------------------------ o que aparece na tela

    private void UpdateLayoutState()
    {
        var overlay = CurrentOverlay();
        UpdateAssistant();

        // Cada aba aparece só se for a página atual do monitor em que está, e nunca junto com um aviso.
        foreach (var tab in _tabs)
        {
            var current = tab.Float is { } floating ? floating.IsShown : tab.Monitor is { } host ? host.Active == tab : _active == tab;
            tab.IsSelected = current;
            tab.View.Visibility = current && overlay is null && tab.BlockedHost is null ? Visibility.Visible : Visibility.Collapsed;
            TrackRest(tab);
        }

        foreach (var monitor in _monitors)
        {
            monitor.Render(overlay?.Title, _tabs.Count > 0, _local);
        }

        if (overlay is { } o)
        {
            OverlayTitle.Text = o.Title;
            OverlayText.Text = o.Text;
            OverlayWrench.Visibility = o.Maintenance ? Visibility.Visible : Visibility.Collapsed;
            OverlayAlert.Visibility = o.Maintenance ? Visibility.Collapsed : Visibility.Visible;
            // Um WebView2 sempre fica por cima do que o WPF desenha; para cobrir tudo, o conteúdo sai de cena.
            // A barra do rodapé continua: é por ela que se desliga o computador.
            Overlay.Visibility = Visibility.Visible;
            ContentHost.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Collapsed;
            HideToast();
            return;
        }

        Overlay.Visibility = Visibility.Collapsed;

        var blocked = _active?.BlockedHost is not null;
        if (blocked)
        {
            var hasPage = _active!.View.Source is { } source && source.Scheme is "http" or "https";
            BlockedText.Text =
                $"Só os sites autorizados pela Pontual abrem neste computador. A tentativa de abrir {_active.BlockedHost} foi registrada" +
                (_local ? "." : " e o TI foi avisado.");
            BlockedBackButton.Content = hasPage ? $"Voltar para {_active.Name}" : "Voltar ao início";
        }

        BlockedPanel.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        ContentHost.Visibility = blocked ? Visibility.Collapsed : Visibility.Visible;
        _home.Visibility = _active is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private (string Title, string Text, bool Maintenance)? CurrentOverlay()
    {
        if (_fatalError is not null)
        {
            return ("O Terminal não pôde continuar", _fatalError, false);
        }

        if (_local)
        {
            return _config is null && _localConfigError is not null
                ? ("A lista de sites não pôde ser lida",
                    $"{_localConfigError}\n\nArquivo: {_localConfigPath}\nAssim que o arquivo for corrigido, esta tela muda sozinha.", false)
                : null;
        }

        if (_state == HeartbeatResponse.StateMaintenance)
        {
            var text = "O TI está trabalhando nesta máquina.";
            var reason = _maintenance?.Reason?.Trim().TrimEnd('.');
            if (!string.IsNullOrEmpty(reason))
            {
                text += $" Motivo: {reason}.";
            }

            if (DateTimeOffset.TryParse(_maintenance?.Until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
            {
                var local = until.ToLocalTime();
                text += local.Date == DateTime.Today
                    ? $" Previsão de retorno: {local:HH:mm}."
                    : $" Previsão de retorno: {local:dd/MM} às {local:HH:mm}.";
            }

            return ("Computador em manutenção", text, true);
        }

        if (_homeFailed)
        {
            return ("Sem conexão com o servidor",
                $"O Terminal não conseguiu abrir a tela inicial em {_homeOrigin}. Ele tenta de novo sozinho a cada 15 segundos.", false);
        }

        return null;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        foreach (var bar in Bars)
        {
            bar.SetClock(now);
        }
    }

    private void UpdateAttendant()
    {
        var name = string.IsNullOrWhiteSpace(_attendantName) ? Environment.MachineName : _attendantName;
        foreach (var bar in Bars)
        {
            bar.SetAttendant(name);
        }
    }

    private void ShowToast(string message)
    {
        if (Overlay.Visibility == Visibility.Visible || !IsLoaded)
        {
            return;
        }

        ToastText.Text = message;
        Toast.HorizontalOffset = Math.Max(0, Root.ActualWidth - 380 - 28 - 16);
        Toast.VerticalOffset = 16;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.IsOpen = false;
    }

    // ------------------------------------------------------------------ barra

    private void CallHelp()
    {
        Record(DeviceEvent.Help);
        ShowToast(_local
            ? "Modo local, sem servidor configurado: o chamado ficou só registrado neste computador. Avise o TI de outra forma."
            : _online == false
                ? "Sem conexão agora. O chamado vai ser enviado ao TI assim que a conexão voltar."
                : "Chamado enviado. O TI já foi avisado.");
        // Pausa curta para um clique repetido não virar vários chamados.
        foreach (var bar in Bars)
        {
            bar.SetHelpEnabled(false);
        }

        var pause = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        pause.Tick += (_, _) =>
        {
            pause.Stop();
            foreach (var bar in Bars)
            {
                bar.SetHelpEnabled(true);
            }
        };
        pause.Start();
    }

    private void OnBlockedBackClick(object sender, RoutedEventArgs e)
    {
        if (_active is { } tab)
        {
            ResolveBlocked(tab);
        }
    }

    /// <summary>Botão "Voltar" do aviso de site bloqueado, no monitor principal ou num secundário.</summary>
    private void ResolveBlocked(SiteTab tab)
    {
        tab.BlockedHost = null;
        var hasPage = tab.View.Source is { } source && source.Scheme is "http" or "https";
        if (hasPage)
        {
            // A navegação foi cancelada: a página em que a atendente estava continua lá.
            Activate(tab);
        }
        else
        {
            // O bloqueio foi logo na entrada: não há página para onde voltar.
            CloseTab(tab);
        }
    }
}
