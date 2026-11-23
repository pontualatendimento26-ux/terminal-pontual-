using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Modo bloqueado: a barra do rodapé numa janela própria, "sempre visível", na faixa de baixo do monitor.
/// Assim ela fica por cima de qualquer programa ou navegador, mesmo de uma janela solta ou em tela cheia (F11),
/// como a barra de tarefas do Windows. A janela do Terminal deixa um espaço vazio do mesmo tamanho embaixo.
/// </summary>
internal sealed class BarWindow : Window
{
    /// <summary>Altura da barra em unidades do WPF (TerminalBar.xaml).</summary>
    public const double BarHeight = 60;

    private NativeMethods.Rect _monitor;
    private bool _placed;
    private bool _allowClose;

    public BarWindow(TerminalBar bar)
    {
        Title = "Terminal Pontual - barra";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.White;
        UseLayoutRounding = true;
        Width = 800;
        Height = BarHeight;
        Content = bar;
        DpiChanged += (_, _) => Place(_monitor);
    }

    /// <summary>Encosta a barra no rodapé do monitor (medidas em pixels, direto do Windows).</summary>
    public void Place(NativeMethods.Rect monitor)
    {
        _monitor = monitor;
        _placed = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var height = (int)Math.Ceiling(BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, monitor.Left, monitor.Bottom - height,
            monitor.Right - monitor.Left, height, NativeMethods.SwpNoActivate);
    }

    /// <summary>Outro programa "sempre visível" pode ter passado na frente: a barra volta para o topo, sem roubar o foco.</summary>
    public void KeepOnTop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && IsVisible)
        {
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    /// <summary>Fecha de verdade (sem passar pela trava do Alt+F4).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Fora do Alt+Tab, como a barra de tarefas.
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, style | NativeMethods.WsExToolWindow);
        if (_placed)
        {
            Place(_monitor);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}

internal static partial class NativeMethods
{
    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
