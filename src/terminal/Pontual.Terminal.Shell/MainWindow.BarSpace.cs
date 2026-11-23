using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Espaço da barra do rodapé. No modo bloqueado não existe a barra de tarefas do Windows, então a "área de trabalho"
/// de cada monitor é a tela inteira e um navegador (ou o Excel) maximizado cobre a nossa barra. Aqui a faixa da barra
/// é tirada da área de trabalho, como a barra do Windows faz: os programas maximizados param acima dela.
/// A janela do Terminal continua ocupando o monitor inteiro (ela mesma desenha a barra nessa faixa).
/// </summary>
public partial class MainWindow
{
    /// <summary>Altura da barra (TerminalBar.xaml), usada enquanto ela ainda não foi medida.</summary>
    private const double BarHeightFallback = 60;

    private bool _barSpaceReserved;

    /// <summary>Reserva a faixa da barra em cada monitor que tem uma janela do Terminal. Só no modo bloqueado.</summary>
    private void ReserveBarSpace()
    {
        if (!_locked || _windowed || _allowClose)
        {
            return;
        }

        try
        {
            var windows = new List<(Window Window, TerminalBar Bar)> { (this, Bar) };
            windows.AddRange(_monitors.Select(m => ((Window)m, m.Bar)));
            foreach (var (window, bar) in windows)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
                var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    continue;
                }

                // Altura da barra em pixels deste monitor (a escala da tela pode ser 125%, 150%...).
                var height = bar.ActualHeight > 0 ? bar.ActualHeight : BarHeightFallback;
                var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                var pixels = (int)Math.Ceiling(height * scale);

                var work = info.Monitor;
                work.Bottom -= pixels;
                if (!SameRect(work, info.Work))
                {
                    SetWorkArea(work);
                }
            }

            _barSpaceReserved = true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("espaço da barra", ex);
        }
    }

    /// <summary>Devolve a tela inteira aos programas (ao fechar o Terminal).</summary>
    private void ReleaseBarSpace()
    {
        if (!_barSpaceReserved)
        {
            return;
        }

        _barSpaceReserved = false;
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.Rect _, IntPtr _) =>
            {
                var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref info) && !SameRect(info.Monitor, info.Work))
                {
                    SetWorkArea(info.Monitor);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("espaço da barra", ex);
        }
    }

    /// <summary>
    /// A janela do Terminal maximizada ocupa o monitor inteiro, não só a área de trabalho: é ela que desenha a barra
    /// na faixa reservada. Responde à pergunta do Windows sobre o tamanho maximizado (WM_GETMINMAXINFO).
    /// </summary>
    private void FillMonitorWhenMaximized(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var limits = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        limits.MaxPosition = new NativeMethods.Point { X = 0, Y = 0 };
        limits.MaxSize = new NativeMethods.Point { X = info.Monitor.Right - info.Monitor.Left, Y = info.Monitor.Bottom - info.Monitor.Top };
        Marshal.StructureToPtr(limits, lParam, fDeleteOld: false);
    }

    private static bool SameRect(NativeMethods.Rect a, NativeMethods.Rect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static void SetWorkArea(NativeMethods.Rect work)
    {
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SpiSetWorkArea, 0, ref work, NativeMethods.SpifSendChange))
        {
            Log.Info($"Não foi possível reservar a faixa da barra (erro {Marshal.GetLastWin32Error()}).");
        }
    }
}

internal static partial class NativeMethods
{
    public const int WmGetMinMaxInfo = 0x0024;
    public const uint SpiSetWorkArea = 0x002F;
    public const uint SpifSendChange = 0x0002;
    public const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref Rect rect, uint flags);
}
