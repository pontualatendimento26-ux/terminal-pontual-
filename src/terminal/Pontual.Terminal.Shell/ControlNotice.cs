using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Plaquinha pequena e discreta no canto superior direito, enquanto o TI está controlando o mouse e o teclado.
/// Fica sempre por cima, não entra no Alt+Tab, não aceita foco nem clique e não pode ser arrastada nem fechada:
/// é um aviso, não uma janela. Aparece quando o controle começa e some quando acaba.
/// </summary>
internal sealed class ControlNotice : Window
{
    private bool _allowClose;

    public ControlNotice(string viewer)
    {
        Title = "Terminal Pontual";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        Focusable = false;

        var texto = string.IsNullOrWhiteSpace(viewer) ? "TI controlando" : $"{viewer} (TI) controlando";
        var corpo = new Border
        {
            // Cor de atenção, mas plaquinha pequena: letra miúda, cantos arredondados, bem no canto da tela.
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xB4, 0x23, 0x18)),
            CornerRadius = new CornerRadius(0, 0, 0, 8),
            Padding = new Thickness(10, 4, 10, 5),
            Child = new TextBlock
            {
                Text = texto,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            },
        };
        Content = corpo;
    }

    /// <summary>Coloca a plaquinha encostada no alto, à direita do monitor principal.</summary>
    public void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Top + 6;
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Fora do Alt+Tab e transparente ao clique no nível do Windows (o mouse passa direto para o que está embaixo).
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExTransparent | WsExNoActivate);
        PlaceTopRight();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceTopRight();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
