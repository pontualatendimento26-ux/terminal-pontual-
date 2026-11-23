using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Aplicativos da Microsoft Store na conta bloqueada (23/11/2026). O Windows só abre aplicativos da Loja com o Explorer
/// rodando como área de trabalho: num shell próprio, como o do bloqueio, eles abrem e fecham na hora (Calculadora) ou o
/// gerenciador de ativação recusa com 0x87B20C15 (Notas Autoadesivas e Microsoft Store no PC da Kamila). A Microsoft diz:
/// "não é possível usar um shell personalizado para abrir aplicativos Universais do Windows".
/// <para>
/// Quando o TI liga "Aplicativos da Microsoft Store" para o computador na Central (<see cref="TerminalConfig.StoreApps"/>),
/// o Terminal liga o Explorer sem pasta, para ele virar a área de trabalho, e o mantém escondido: a barra de tarefas é
/// escondida assim que aparece, as teclas Windows e Ctrl+Esc (que abririam o Iniciar) não chegam ao Windows, e a faixa da
/// barra do Terminal volta a ser reservada. O resultado vai para o histórico do computador na Central (evento store_shell).
/// Ligado, vale até a atendente sair da conta.
/// </para>
/// </summary>
public partial class MainWindow
{
    private bool _storeShellOn;
    private DispatcherTimer? _storeShellTimer;
    private IntPtr _keyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _showHook;
    private NativeMethods.WinEventProc? _showProc;
    /// <summary>Estado da barra de tarefas antes de o Terminal ligar o "ocultar automaticamente" (para devolver ao sair).</summary>
    private int? _taskbarStateBefore;

    private void ApplyStoreShell(TerminalConfig? config)
    {
        if (!_locked || _windowed || _local || config?.StoreApps != true || _storeShellOn)
        {
            return;
        }

        _storeShellOn = true;
        BlockStartKeys();
        WatchTaskbarShow();
        // Reserva: o aviso do Windows (WatchTaskbarShow) esconde na hora; a conferência periódica cobre o que escapar.
        _storeShellTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _storeShellTimer.Tick += (_, _) => HideTaskbars();
        _storeShellTimer.Start();

        if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
        {
            HideTaskbars();
            Log.Info("Explorer já estava rodando como área de trabalho; barra de tarefas escondida.");
            Record(DeviceEvent.StoreShell, "Microsoft Store", "o Explorer já estava rodando escondido; os aplicativos da Loja podem abrir");
            return;
        }

        _ = StartHiddenExplorerAsync();
    }

    private async Task StartHiddenExplorerAsync()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        int pid;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false });
            pid = process?.Id ?? 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("ligar o Explorer escondido", ex);
            Record(DeviceEvent.StoreShell, "Microsoft Store", $"não deu para ligar o Explorer: {ex.Message}");
            return;
        }

        // A barra de tarefas aparece quando o Explorer vira a área de trabalho (em poucos segundos).
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
            {
                HideTaskbars();
                Log.Info($"Explorer escondido rodando como área de trabalho (processo {pid}).");
                Record(DeviceEvent.StoreShell, "Microsoft Store", "o Explorer está rodando escondido como área de trabalho; os aplicativos da Loja podem abrir");
                return;
            }
        }

        // O Windows não aceitou o Explorer como área de trabalho: ele abriu só uma janela de pastas, que é fechada.
        CloseFolderWindows(pid);
        Log.Info("O Explorer abriu, mas o Windows não o aceitou como área de trabalho.");
        Record(DeviceEvent.StoreShell, "Microsoft Store", "o Windows não aceitou o Explorer como área de trabalho nesta conta; os aplicativos da Loja continuam sem abrir");
    }

    /// <summary>Esconde a barra de tarefas de todos os monitores e devolve a faixa da barra do Terminal.</summary>
    private void HideTaskbars()
    {
        var hidden = false;
        NativeMethods.EnumWindows((handle, _) =>
        {
            var name = ClassName(handle);
            if (name == "Shell_TrayWnd")
            {
                AutoHideTaskbar(handle);
            }

            if ((name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") && NativeMethods.IsWindowVisible(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwHide);
                hidden = true;
            }

            return true;
        }, IntPtr.Zero);

        if (hidden)
        {
            // O Explorer reserva a faixa da barra de tarefas dele; a área de trabalho volta a ser a do Terminal.
            ReserveBarSpace();
        }
    }

    /// <summary>
    /// A barra de tarefas do Explorer volta a aparecer sozinha quando abre uma janela nova (um aplicativo do Chrome,
    /// por exemplo). Conferir de tempos em tempos deixava ela piscar; aqui o Windows avisa no instante em que ela
    /// aparece e ela é escondida na mesma hora.
    /// </summary>
    private void WatchTaskbarShow()
    {
        if (_showHook != IntPtr.Zero)
        {
            return;
        }

        _showProc = (_, _, handle, idObject, idChild, _, _) =>
        {
            if (idObject != 0 || idChild != 0 || handle == IntPtr.Zero)
            {
                return;
            }

            var name = ClassName(handle);
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwHide);
                ReserveBarSpace();
            }
        };
        _showHook = NativeMethods.SetWinEventHook(NativeMethods.EventObjectShow, NativeMethods.EventObjectShow, IntPtr.Zero, _showProc,
            0, 0, NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        if (_showHook == IntPtr.Zero)
        {
            Log.Info("Não deu para receber o aviso de quando a barra de tarefas aparece; fica só a conferência periódica.");
        }
    }

    /// <summary>
    /// Com "ocultar automaticamente", a barra do Explorer deixa de tomar a faixa de baixo da tela: os programas
    /// maximizados não mudam mais de tamanho cada vez que ela tenta aparecer. Só nesta conta, enquanto o Terminal roda.
    /// </summary>
    private void AutoHideTaskbar(IntPtr tray)
    {
        var data = new NativeMethods.AppBarData { Size = (uint)Marshal.SizeOf<NativeMethods.AppBarData>(), Window = tray };
        var state = (int)NativeMethods.SHAppBarMessage(NativeMethods.AbmGetState, ref data);
        if ((state & NativeMethods.AbsAutoHide) != 0)
        {
            return;
        }

        _taskbarStateBefore ??= state;
        data.Param = new IntPtr(state | NativeMethods.AbsAutoHide);
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetState, ref data);
        Log.Info("Barra de tarefas do Explorer em ocultar automaticamente.");
    }

    private void RestoreTaskbarState()
    {
        if (_taskbarStateBefore is not { } before)
        {
            return;
        }

        var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            return;
        }

        var data = new NativeMethods.AppBarData { Size = (uint)Marshal.SizeOf<NativeMethods.AppBarData>(), Window = tray, Param = new IntPtr(before) };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetState, ref data);
        _taskbarStateBefore = null;
    }

    private static void CloseFolderWindows(int pid)
    {
        if (pid == 0)
        {
            return;
        }

        NativeMethods.EnumWindows((handle, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var owner);
            if (owner == pid && ClassName(handle) == "CabinetWClass")
            {
                NativeMethods.PostMessage(handle, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static string ClassName(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(64);
        return NativeMethods.GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>As teclas Windows e Ctrl+Esc abririam o Iniciar do Explorer: o Terminal as segura antes do Windows.</summary>
    private void BlockStartKeys()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        _keyboardProc = KeyboardHook;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            Log.Info($"Não deu para travar as teclas do Iniciar (erro {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0;
            if (key is NativeMethods.VkLWin or NativeMethods.VkRWin || (key == NativeMethods.VkEscape && ctrl))
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void StopStoreShell()
    {
        _storeShellTimer?.Stop();
        if (_showHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }

        RestoreTaskbarState();
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }
}

internal static partial class NativeMethods
{
    public const int SwHide = 0;
    public const uint WmClose = 0x0010;
    public const int WhKeyboardLl = 13;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkEscape = 0x1B;
    public const int VkControl = 0x11;

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    public const uint EventObjectShow = 0x8002;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const uint AbmGetState = 0x04;
    public const uint AbmSetState = 0x0A;
    public const int AbsAutoHide = 0x01;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public IntPtr Param;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);
}
