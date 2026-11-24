using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Atalhos do Windows que a conta bloqueada perde porque o Explorer não é a área de trabalho (e NoWinKeys desliga as teclas
/// Windows): o Terminal faz o trabalho deles.
/// <list type="bullet">
/// <item>PrtSc: copia a tela inteira (Alt+PrtSc: só a janela da frente) para a área de transferência; cola com Ctrl+V.</item>
/// <item>Win+V: histórico da área de transferência (os últimos itens copiados nesta sessão, só na memória; nada vai para o disco).</item>
/// </list>
/// O mesmo gancho de teclado segura as teclas Windows e Ctrl+Esc quando o Explorer escondido está ligado (<see cref="BlockStartKeys"/>).
/// </summary>
public partial class MainWindow
{
    private const int MaxClipboardItems = 25;
    private const int MaxClipboardText = 200_000;

    private bool _blockStartKeys;
    private bool _winDown;
    private bool _swallowV;
    private bool _swallowSnapshot;
    private bool _ignoreNextClipboard;
    private readonly List<ClipboardItem> _clipboardHistory = [];
    private ClipboardHistoryWindow? _clipboardWindow;

    /// <summary>Liga o gancho de teclado e o vigia da área de transferência (só na conta bloqueada).</summary>
    private void StartShortcuts()
    {
        if (!_locked || _hwndSource is null)
        {
            return;
        }

        InstallKeyboardHook();
        if (NativeMethods.AddClipboardFormatListener(_hwndSource.Handle))
        {
            _hwndSource.AddHook(ClipboardWndProc);
        }
        else
        {
            Log.Info($"Não deu para acompanhar a área de transferência (erro {Marshal.GetLastWin32Error()}); o Win+V fica sem histórico.");
        }
    }

    private void StopShortcuts()
    {
        if (_hwndSource is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(ClipboardWndProc);
        }

        _clipboardWindow?.Close();
        _clipboardWindow = null;
        _clipboardHistory.Clear();
    }

    /// <summary>As teclas Windows e Ctrl+Esc abririam o Iniciar do Explorer: o Terminal as segura antes do Windows.</summary>
    private void BlockStartKeys()
    {
        _blockStartKeys = true;
        InstallKeyboardHook();
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        _keyboardProc = KeyboardHook;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            Log.Info($"Não deu para ligar os atalhos de teclado (erro {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var msg = message.ToInt32();
            var down = msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;

            if (key is NativeMethods.VkLWin or NativeMethods.VkRWin)
            {
                _winDown = down;
                if (_blockStartKeys)
                {
                    return new IntPtr(1);
                }
            }
            else if (key == NativeMethods.VkV && (_winDown || _swallowV))
            {
                // Win+V: o Terminal mostra o histórico; o "v" não chega à janela da frente.
                if (down && !_swallowV)
                {
                    Dispatcher.BeginInvoke(ShowClipboardHistory);
                }

                _swallowV = down;
                return new IntPtr(1);
            }
            else if (key == NativeMethods.VkSnapshot && PassSnapshotKey(down))
            {
                // Print seletivo: a tecla segue para o Lightshot (MainWindow.Lightshot.cs).
            }
            else if (key == NativeMethods.VkSnapshot)
            {
                if (down && !_swallowSnapshot)
                {
                    var onlyWindow = (NativeMethods.GetAsyncKeyState(NativeMethods.VkMenu) & 0x8000) != 0;
                    var target = onlyWindow ? ChromeNative.GetForegroundWindow() : IntPtr.Zero;
                    Dispatcher.BeginInvoke(() => CopyScreenToClipboard(target));
                }

                _swallowSnapshot = down;
                return new IntPtr(1);
            }
            else if (_blockStartKeys && key == NativeMethods.VkEscape && (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0)
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    // ------------------------------------------------------------------ PrtSc

    private void CopyScreenToClipboard(IntPtr window)
    {
        try
        {
            var image = ScreenCapture.Image(window);
            if (!TrySetClipboard(() => Clipboard.SetImage(image)))
            {
                ShowToast("Não deu para copiar a tela. Tente de novo.");
                return;
            }

            ShowToast(window == IntPtr.Zero ? "Tela copiada. Cole com Ctrl+V." : "Janela copiada. Cole com Ctrl+V.");
        }
        catch (Exception ex)
        {
            Log.Error("PrtSc", ex);
            ShowToast("Não deu para copiar a tela. Tente de novo.");
        }
    }

    // ------------------------------------------------------------------ Win+V

    private IntPtr ClipboardWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
        {
            // Lê depois: quem copiou ainda pode estar com a área de transferência aberta.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ReadClipboard);
        }

        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        if (_ignoreNextClipboard)
        {
            _ignoreNextClipboard = false;
            return;
        }

        ClipboardItem? item = null;
        for (var attempt = 0; attempt < 3 && item is null; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaxClipboardText)
                    {
                        item = new ClipboardItem(text, null);
                    }
                }
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    image.Freeze();
                    item = new ClipboardItem(null, image);
                }

                break;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Log.Error("leitura da área de transferência", ex);
                return;
            }
        }

        if (item is null)
        {
            return;
        }

        if (item.Text is not null)
        {
            _clipboardHistory.RemoveAll(i => i.Text == item.Text);
        }

        _clipboardHistory.Insert(0, item);
        if (_clipboardHistory.Count > MaxClipboardItems)
        {
            _clipboardHistory.RemoveRange(MaxClipboardItems, _clipboardHistory.Count - MaxClipboardItems);
        }
    }

    private void ShowClipboardHistory()
    {
        try
        {
            var previous = ChromeNative.GetForegroundWindow();
            _clipboardWindow?.Close();
            _clipboardWindow = new ClipboardHistoryWindow(_clipboardHistory);
            _clipboardWindow.Chosen += (_, item) => PasteFromHistory(item, previous);
            _clipboardWindow.Cleared += (_, _) => _clipboardHistory.Clear();
            _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
            _clipboardWindow.Show();
            _clipboardWindow.Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(_clipboardWindow).Handle);
        }
        catch (Exception ex)
        {
            Log.Error("Win+V", ex);
        }
    }

    private async void PasteFromHistory(ClipboardItem item, IntPtr previous)
    {
        try
        {
            _clipboardHistory.Remove(item);
            _clipboardHistory.Insert(0, item);
            _ignoreNextClipboard = true;
            var ok = TrySetClipboard(() =>
            {
                if (item.Text is not null)
                {
                    Clipboard.SetText(item.Text);
                }
                else if (item.Image is not null)
                {
                    Clipboard.SetImage(item.Image);
                }
            });
            if (!ok)
            {
                _ignoreNextClipboard = false;
                ShowToast("Não deu para colar. Tente de novo.");
                return;
            }

            if (previous != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(previous);
            }

            await Task.Delay(120);
            ChromeNative.keybd_event(ChromeNative.VkControl, 0, 0, UIntPtr.Zero);
            ChromeNative.keybd_event((byte)NativeMethods.VkV, 0, 0, UIntPtr.Zero);
            ChromeNative.keybd_event((byte)NativeMethods.VkV, 0, ChromeNative.KeyUp, UIntPtr.Zero);
            ChromeNative.keybd_event(ChromeNative.VkControl, 0, ChromeNative.KeyUp, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("colar do histórico", ex);
        }
    }

    private static bool TrySetClipboard(Action set)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                set();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}

/// <summary>Um item copiado: texto ou imagem.</summary>
internal sealed record ClipboardItem(string? Text, BitmapSource? Image);

/// <summary>A janelinha do Win+V: os últimos itens copiados; clique (ou Enter) cola na janela que estava na frente.</summary>
internal sealed class ClipboardHistoryWindow : Window
{
    private bool _closing;

    public ClipboardHistoryWindow(IReadOnlyList<ClipboardItem> items)
    {
        Title = "Área de transferência";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 560;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.White;

        var list = new StackPanel { Margin = new Thickness(8) };
        var header = new DockPanel { Margin = new Thickness(4, 2, 4, 8) };
        var clear = new Button { Content = "Limpar tudo", Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
        clear.Click += (_, _) =>
        {
            Cleared?.Invoke(this, EventArgs.Empty);
            Close();
        };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(new TextBlock { Text = "Área de transferência", FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        list.Children.Add(header);

        if (items.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "Nada copiado ainda. Copie algo (Ctrl+C) e aperte Win+V de novo.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(4, 4, 4, 8),
            });
        }

        Button? first = null;
        foreach (var item in items)
        {
            UIElement content = item.Image is not null
                ? new Image { Source = item.Image, MaxHeight = 90, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform }
                : new TextBlock { Text = Preview(item.Text!), TextWrapping = TextWrapping.Wrap, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis };
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xE0, 0xEA)),
                Cursor = Cursors.Hand,
            };
            var chosen = item;
            button.Click += (_, _) =>
            {
                Close();
                Chosen?.Invoke(this, chosen);
            };
            first ??= button;
            list.Children.Add(button);
        }

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBC, 0xCC)),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
        };

        Loaded += (_, _) =>
        {
            // Perto do mouse, sem sair da tela.
            var area = SystemParameters.WorkArea;
            NativeMethods.GetCursorPos(out var mouse);
            var source = PresentationSource.FromVisual(this);
            var point = source?.CompositionTarget?.TransformFromDevice.Transform(new Point(mouse.X, mouse.Y)) ?? new Point(mouse.X, mouse.Y);
            Left = Math.Clamp(point.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(point.Y - 40, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
            (first as UIElement ?? clear).Focus();
        };
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SafeClose();
            }
        };
    }

    public event EventHandler<ClipboardItem>? Chosen;

    public event EventHandler? Cleared;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void SafeClose()
    {
        if (!_closing)
        {
            Close();
        }
    }

    private static string Preview(string text)
    {
        var clean = text.Trim().Replace("\r", "");
        return clean.Length > 300 ? clean[..300] + "…" : clean;
    }
}

internal static partial class NativeMethods
{
    public const int WmKeyDown = 0x0100;
    public const int WmSysKeyDown = 0x0104;
    public const int WmClipboardUpdate = 0x031D;
    public const int VkV = 0x56;
    public const int VkSnapshot = 0x2C;
    public const int VkMenu = 0x12;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out CursorPoint point);
}
