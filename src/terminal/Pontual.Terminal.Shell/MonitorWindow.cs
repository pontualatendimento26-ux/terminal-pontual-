using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Tela do Terminal num monitor que não é o principal: mostra uma das páginas abertas e a mesma barra do rodapé.
/// A tela inicial fica só no monitor principal. Quem manda aqui é a <see cref="MainWindow"/>.
/// </summary>
internal sealed class MonitorWindow : Window
{
    private readonly Grid _host = new();
    private readonly StackPanel _message = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560 };
    private readonly TextBlock _title = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { FontSize = 17, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _back = new() { Content = "Voltar", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
    private NativeMethods.Rect _bounds;
    private bool _allowClose;

    public MonitorWindow(NativeMethods.Rect bounds)
    {
        _bounds = bounds;
        Title = "Terminal Pontual - segundo monitor";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = (Brush)Application.Current.FindResource("Fundo");
        FontFamily = (FontFamily)Application.Current.FindResource("FonteMarca");
        FontSize = 15;
        UseLayoutRounding = true;

        _title.Foreground = (Brush)Application.Current.FindResource("Tinta");
        _text.Foreground = (Brush)Application.Current.FindResource("Suave");
        _back.Style = (Style)Application.Current.FindResource("BotaoPilula");
        _back.Click += (_, _) =>
        {
            if (Active is { } tab)
            {
                BlockedBack?.Invoke(tab);
            }
        };
        _message.Children.Add(_title);
        _message.Children.Add(_text);
        _message.Children.Add(_back);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_host);
        root.Children.Add(_message);
        Grid.SetRow(Bar, 1);
        root.Children.Add(Bar);
        Content = root;
        Bar.ShowHome(false);
    }

    /// <summary>O botão "Voltar" do aviso de site bloqueado foi clicado.</summary>
    public event Action<SiteTab>? BlockedBack;

    public TerminalBar Bar { get; } = new();

    /// <summary>Onde entram os WebView2 das páginas que estão neste monitor.</summary>
    public Panel Host => _host;

    /// <summary>A página visível neste monitor; nulo quando ele está livre.</summary>
    public SiteTab? Active { get; set; }

    /// <summary>Ocupa o monitor inteiro. As medidas são em pixels, direto do Windows, para não depender da escala da tela.</summary>
    public void Place(NativeMethods.Rect bounds)
    {
        _bounds = bounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    /// <summary>Fecha de verdade (sem passar pela trava do Alt+F4).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Decide o que aparece: a página, ou um aviso no lugar dela (um WebView2 não aceita nada desenhado por cima).
    /// <paramref name="cover"/> é o título do aviso de tela cheia do monitor principal (manutenção, sem conexão), se houver.
    /// </summary>
    public void Render(string? cover, bool anyTab, bool local)
    {
        string? title = null;
        var text = "";
        var back = false;
        if (cover is not null)
        {
            title = cover;
            text = "Veja o aviso no monitor principal.";
        }
        else if (Active is null)
        {
            title = "Monitor livre";
            text = anyTab
                ? "Para usar este monitor, clique numa página aberta na barra aqui embaixo."
                : "Abra um site no monitor principal. Depois, clique nele na barra aqui embaixo para trazê-lo para este monitor.";
        }
        else if (Active.BlockedHost is { } host)
        {
            title = "Este site não é permitido";
            text = $"Só os sites autorizados pela Pontual abrem neste computador. A tentativa de abrir {host} foi registrada" + (local ? "." : " e o TI foi avisado.");
            back = true;
        }

        _title.Text = title ?? "";
        _text.Text = text;
        _back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        _host.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Place(_bounds);
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
