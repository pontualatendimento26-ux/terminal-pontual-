using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>Iniciais e cor do "avatar" de um nome: a mesma conta da tela web, para as cores coincidirem.</summary>
internal static class Brand
{
    private static readonly string[] Colors = ["#0d47a1", "#1976d2", "#2e9e5b", "#0f9d76", "#0c8fd0", "#2e416d", "#4f6fcf", "#0f766e"];

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "?";
        }

        var second = parts.Length > 1 ? parts[1][..1] : parts[0].Length > 1 ? parts[0].Substring(1, 1) : "";
        return (parts[0][..1] + second).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
    }

    public static Brush ColorFor(string name)
    {
        uint hash = 0;
        foreach (var c in name)
        {
            if (!char.IsLowSurrogate(c))
            {
                hash = unchecked((hash * 31) + c);
            }
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors[hash % (uint)Colors.Length]));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Uma aba de site aberta: o WebView2 e o que a barra mostra dela.</summary>
internal sealed class SiteTab(ConfigItem item, WebView2 view) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = item.Id;

    public string Name { get; } = item.Name;

    public string Target { get; } = item.Target;

    public WebView2 View { get; } = view;

    public string Initials { get; } = Brand.Initials(item.Name);

    public Brush Color { get; } = Brand.ColorFor(item.Name);

    public string CloseLabel => $"Fechar {Name}";

    /// <summary>Domínio da última navegação bloqueada; enquanto não for nulo, a aba mostra o aviso de bloqueio.</summary>
    public string? BlockedHost { get; set; }

    public bool IsClosed { get; set; }

    /// <summary>Janela do monitor secundário em que a aba está; nulo quando está na janela principal.</summary>
    public MonitorWindow? Monitor { get; set; }

    public string? LastObservedHost { get; set; }

    public DateTime LastObservedAt { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

internal sealed record RunningProgram(ConfigItem Item, Process Process);

/// <summary>Janela de outro programa (explorador de arquivos, Excel, PDF) mostrada na barra do modo bloqueado.</summary>
internal sealed class OpenWindow(IntPtr handle, string title) : INotifyPropertyChanged
{
    private string _title = title;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IntPtr Handle { get; } = handle;

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }
    }
}

/// <summary>
/// As janelas dos outros programas, como a barra de tarefas do Windows as enxerga: visíveis, com título,
/// sem dona e que não sejam deste programa. No modo bloqueado não existe barra do Windows; a nossa faz esse papel.
/// </summary>
internal static class OtherWindows
{
    public static List<(IntPtr Handle, string Title)> List()
    {
        var result = new List<(IntPtr, string)>();
        var me = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)
                || NativeMethods.GetWindow(handle, NativeMethods.GwOwner) != IntPtr.Zero
                || (NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle) & NativeMethods.WsExToolWindow) != 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == me)
            {
                return true;
            }

            // Janelas "fantasma" de aplicativos da loja: existem, mas o Windows as mantém escondidas.
            if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var length = NativeMethods.GetWindowTextLength(handle);
            if (length <= 0)
            {
                return true;
            }

            var text = new System.Text.StringBuilder(length + 1);
            NativeMethods.GetWindowText(handle, text, text.Capacity);
            if (text.Length > 0)
            {
                result.Add((handle, text.ToString()));
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void BringToFront(IntPtr handle)
    {
        if (NativeMethods.IsIconic(handle))
        {
            NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
        }

        NativeMethods.SetForegroundWindow(handle);
    }
}

/// <summary>Os monitores ligados ao computador, em pixels da área de trabalho.</summary>
internal static class Monitors
{
    /// <summary>Retângulo de cada monitor que não é o principal, da esquerda para a direita.</summary>
    public static List<NativeMethods.Rect> Secondary()
    {
        var result = new List<NativeMethods.Rect>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.Rect _, IntPtr _) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info) && (info.Flags & NativeMethods.MonitorInfoPrimary) == 0)
            {
                result.Add(info.Monitor);
            }

            return true;
        }, IntPtr.Zero);
        result.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
        return result;
    }

    /// <summary>Retângulo do monitor principal (onde fica a janela principal do Terminal).</summary>
    public static NativeMethods.Rect Primary()
    {
        var result = new NativeMethods.Rect();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.Rect _, IntPtr _) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info) && (info.Flags & NativeMethods.MonitorInfoPrimary) != 0)
            {
                result = info.Monitor;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }
}

internal static class WindowsInfo
{
    /// <summary>Algo como "Windows 10 Pro 22H2". O registro ainda diz "Windows 10" no Windows 11; o número da compilação desfaz a dúvida.</summary>
    public static string Describe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(product))
            {
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                }

                return string.IsNullOrWhiteSpace(display) ? product : $"{product} {display}";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Sem acesso ao registro: usa a versão do sistema.
        }

        return $"Windows {Environment.OSVersion.Version}";
    }
}

/// <summary>Diário de erros do programa, em <c>%LocalAppData%\Pontual\Terminal\terminal.log</c>. Nunca lança.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(EnrollmentStore.DefaultDirectory, "terminal.log");

    public static void Error(string where, Exception ex) => Write($"ERRO {where}: {ex.GetType().Name}: {ex.Message}");

    public static void Info(string message) => Write(message);

    private static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(EnrollmentStore.DefaultDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 2_000_000)
                {
                    file.Delete();
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem onde escrever, segue sem diário.
        }
    }
}

internal static partial class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;
    public const uint VkQ = 0x51;
    public const uint VkP = 0x50;
    public const int VkSpace = 0x20;

    public const uint GwOwner = 4;
    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x80;
    public const int DwmwaCloaked = 14;
    public const int MonitorInfoPrimary = 1;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Bit mais alto ligado: a tecla está apertada agora.</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
    public const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
