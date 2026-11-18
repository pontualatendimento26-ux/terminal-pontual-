using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Web.WebView2.Wpf;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Botão flutuante da assistente. É uma janela própria (e não um desenho dentro da janela principal)
/// porque um WebView2 sempre fica por cima do que o WPF desenha: só outra janela aparece sobre os sites.
/// Pode ser arrastado; um clique sem arrastar abre ou fecha a conversa.
/// </summary>
internal sealed class AssistantBubble : Window
{
    /// <summary>Lado da janela: o círculo de 56 mais a folga da sombra.</summary>
    public const double Side = 76;

    public AssistantBubble()
    {
        Title = "Assistente Pontual";
        Width = Side;
        Height = Side;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Cursor = Cursors.Hand;
        ToolTip = "Assistente Pontual: tire dúvidas sobre os roteiros dos bancos";
        AutomationProperties.SetName(this, "Abrir a Assistente Pontual");

        var brand = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brand.GradientStops.Add(new GradientStop(Color.FromRgb(0x0D, 0x47, 0xA1), 0));
        brand.GradientStops.Add(new GradientStop(Color.FromRgb(0x19, 0x76, 0xD2), 0.55));
        brand.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 1));

        var icon = new Canvas { Width = 24, Height = 24 };
        icon.Children.Add(Spark("M12 3l1.8 4.7L18.5 9.5l-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8L12 3Z"));
        icon.Children.Add(Spark("M18.5 15.5l.8 2 2 .8-2 .8-.8 2-.8-2-2-.8 2-.8.8-2Z"));

        Content = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = brand,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.32, Color = Color.FromRgb(0x0F, 0x17, 0x2A) },
            Child = new Viewbox { Width = 28, Height = 28, Child = icon },
        };

        MouseLeftButtonDown += OnPress;
    }

    /// <summary>Clique (sem arrastar).</summary>
    public event EventHandler? Clicked;

    /// <summary>O botão foi arrastado para outro lugar.</summary>
    public event EventHandler? Moved;

    private static System.Windows.Shapes.Path Spark(string data) => new()
    {
        Data = Geometry.Parse(data),
        Fill = Brushes.White,
        Stroke = Brushes.White,
        StrokeThickness = 1.2,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        var left = Left;
        var top = Top;
        try
        {
            // Só volta quando o botão do mouse é solto.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // O botão já tinha sido solto.
        }

        if (Math.Abs(Left - left) < 4 && Math.Abs(Top - top) < 4)
        {
            Left = left;
            Top = top;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Janela da conversa: um WebView2 com a página <c>/terminal/assistente.html</c> do servidor.</summary>
internal sealed class AssistantPanel : Window
{
    public const double PanelWidth = 400;
    public const double PanelHeight = 620;

    private bool _allowClose;

    public AssistantPanel()
    {
        Title = "Assistente Pontual";
        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LinhaForte"),
            BorderThickness = new Thickness(1),
            Child = View,
        };
    }

    public WebView2 View { get; } = new();

    /// <summary>Fecha de verdade; o fechar comum (Alt+F4) só esconde, para a conversa não se perder.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Cantos arredondados do Windows 11. No Windows 10 a chamada falha e a janela fica reta.
        var round = NativeMethods.DwmwcpRound;
        _ = NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, NativeMethods.DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        View.Dispose();
        base.OnClosed(e);
    }
}

internal static partial class NativeMethods
{
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
