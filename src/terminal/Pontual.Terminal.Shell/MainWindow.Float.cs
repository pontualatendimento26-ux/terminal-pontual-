using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Sites em janela solta (pedido do dono, 27/11/2026: "ficam travados, não conseguem aumentar o tamanho da aba").
/// Cada site abre ocupando a tela inteira; o botão de janela na aba da barra solta o site numa janela comum do Windows,
/// que a atendente move, aumenta, diminui, maximiza e põe lado a lado com outra. O mesmo botão devolve à tela inteira.
/// O site continua o mesmo WebView2, com a mesma política de endereços: só muda onde ele aparece.
/// Aqui ficam também as teclas de navegador que os sites voltam a ter: zoom, recarregar e voltar/avançar.
/// </summary>
public partial class MainWindow
{
    private const double ZoomStep = 1.1;
    private const double ZoomMin = 0.5;
    private const double ZoomMax = 3.0;

    private void ToggleFloat(SiteTab tab)
    {
        if (tab.Float is null)
        {
            FloatTab(tab);
        }
        else
        {
            DockTab(tab);
        }
    }

    /// <summary>Tira a aba da tela inteira e mostra numa janela comum, no monitor em que ela estava.</summary>
    private void FloatTab(SiteTab tab)
    {
        if (tab.IsClosed || tab.Float is not null)
        {
            return;
        }

        Window source = (Window?)tab.Monitor ?? this;
        HostOf(tab.Monitor).Children.Remove(tab.View);
        if (tab.Monitor is { } monitor)
        {
            if (monitor.Active == tab)
            {
                monitor.Active = null;
            }
        }
        else if (_active == tab)
        {
            _active = null;
        }

        tab.Monitor = null;
        tab.BlockedHost = null;

        var window = new FloatWindow(tab.Name, tab.View) { Owner = this };
        window.PlaceOver(source);
        window.Closed += (_, _) =>
        {
            // Fechada pelo X: o site fecha junto, como em qualquer navegador. (Ao voltar para a tela inteira, a aba já saiu daqui.)
            if (ReferenceEquals(tab.Float, window))
            {
                tab.Float = null;
                CloseTab(tab);
            }
        };
        window.ShownChanged += UpdateLayoutState;
        tab.Float = window;
        window.Show();
        UpdateLayoutState();
        tab.View.Focus();
    }

    /// <summary>Devolve a aba à tela inteira do monitor principal.</summary>
    private void DockTab(SiteTab tab)
    {
        if (tab.Float is not { } window)
        {
            return;
        }

        tab.Float = null;
        window.Release();
        window.Close();
        if (tab.IsClosed)
        {
            return;
        }

        tab.Monitor = null;
        ContentHost.Children.Add(tab.View);
        ShowTab(tab, monitor: null);
    }

    /// <summary>
    /// Teclas de navegador dos sites. As do próprio WebView2 ficam desligadas (abririam impressão, código-fonte, ferramentas);
    /// o Terminal atende só estas: Ctrl + e Ctrl - (zoom), Ctrl 0 (tamanho normal), F5 ou Ctrl R (recarregar),
    /// Alt ← e Alt → (voltar e avançar). Ctrl + roda do mouse já funcionava.
    /// </summary>
    private static void WireSiteKeys(WebView2 view) => view.KeyDown += OnSiteKey;

    private static void OnSiteKey(object sender, KeyEventArgs e)
    {
        if (sender is not WebView2 { CoreWebView2: { } core } view)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        try
        {
            switch (key)
            {
                case Key.Add or Key.OemPlus when modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift):
                    view.ZoomFactor = Math.Min(ZoomMax, view.ZoomFactor * ZoomStep);
                    break;
                case Key.Subtract or Key.OemMinus when modifiers == ModifierKeys.Control:
                    view.ZoomFactor = Math.Max(ZoomMin, view.ZoomFactor / ZoomStep);
                    break;
                case Key.D0 or Key.NumPad0 when modifiers == ModifierKeys.Control:
                    view.ZoomFactor = 1.0;
                    break;
                case Key.F5 when modifiers == ModifierKeys.None:
                case Key.R when modifiers == ModifierKeys.Control:
                    core.Reload();
                    break;
                case Key.Left when modifiers == ModifierKeys.Alt:
                    if (core.CanGoBack)
                    {
                        core.GoBack();
                    }

                    break;
                case Key.Right when modifiers == ModifierKeys.Alt:
                    if (core.CanGoForward)
                    {
                        core.GoForward();
                    }

                    break;
                default:
                    return;
            }

            e.Handled = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("tecla do site", ex);
        }
    }
}

/// <summary>
/// Janela comum do Windows com um site dentro: barra de título, minimizar, maximizar, fechar e bordas que redimensionam.
/// Fica sempre na frente da janela principal do Terminal (é filha dela), para não sumir atrás da tela inteira.
/// Minimizar esconde a janela (no modo bloqueado não há barra de tarefas do Windows); a aba na barra do Terminal a traz de volta.
/// </summary>
internal sealed class FloatWindow : Window
{
    private readonly Grid _host = new();
    private WindowState _restoreState = WindowState.Normal;

    public FloatWindow(string title, WebView2 view)
    {
        Title = title;
        MinWidth = 420;
        MinHeight = 320;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        _host.Children.Add(view);
        Content = _host;
        IsVisibleChanged += (_, _) => ShownChanged?.Invoke();
    }

    /// <summary>A janela apareceu ou foi escondida (minimizada).</summary>
    public event Action? ShownChanged;

    /// <summary>Na tela agora (não minimizada).</summary>
    public bool IsShown => IsVisible;

    /// <summary>Tamanho inicial: cerca de três quartos da janela de origem, centralizada nela (no mesmo monitor).</summary>
    public void PlaceOver(Window source)
    {
        var width = source.ActualWidth > 0 ? source.ActualWidth : SystemParameters.WorkArea.Width;
        var height = source.ActualHeight > 0 ? source.ActualHeight : SystemParameters.WorkArea.Height;
        var origin = new Point(0, 0);
        try
        {
            if (PresentationSource.FromVisual(source) is { CompositionTarget: { } target })
            {
                origin = target.TransformFromDevice.Transform(source.PointToScreen(new Point(0, 0)));
            }
        }
        catch (InvalidOperationException)
        {
            // Janela de origem ainda sem tela: fica no canto do monitor principal.
        }

        Width = Math.Max(MinWidth, width * 0.75);
        Height = Math.Max(MinHeight, (height - BarWindow.BarHeight) * 0.8);
        Left = origin.X + Math.Max(0, (width - Width) / 2);
        Top = origin.Y + Math.Max(0, (height - BarWindow.BarHeight - Height) / 2);
    }

    /// <summary>Mostra de novo (se estava minimizada) e traz para a frente.</summary>
    public void BringFront()
    {
        if (!IsVisible)
        {
            WindowState = _restoreState;
            Show();
        }
        else if (WindowState == WindowState.Minimized)
        {
            WindowState = _restoreState;
        }

        Activate();
    }

    /// <summary>Solta o site, que vai para outro lugar (tela inteira) ou é descartado pela janela principal.</summary>
    public void Release() => _host.Children.Clear();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            // Sem barra de tarefas, a janela minimizada viraria um toco no canto da tela: fica escondida.
            Dispatcher.BeginInvoke(() =>
            {
                if (WindowState == WindowState.Minimized && IsVisible)
                {
                    Hide();
                }
            });
        }
        else
        {
            _restoreState = WindowState;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            // O site é descartado pela janela principal, não por esta.
            Release();
        }
    }
}
