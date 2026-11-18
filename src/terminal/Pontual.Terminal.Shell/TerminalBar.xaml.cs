using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Barra do rodapé do Terminal, igual em todos os monitores. Ela só mostra e avisa dos cliques;
/// quem decide o que fazer é a <see cref="MainWindow"/>.
/// </summary>
public partial class TerminalBar : UserControl
{
    public TerminalBar()
    {
        InitializeComponent();
    }

    internal event Action? HomeClick;

    internal event Action? HelpClick;

    internal event Action? FilesClick;

    internal event Action? PowerClick;

    internal event Action<SiteTab>? TabClick;

    internal event Action<SiteTab>? TabCloseClick;

    internal event Action<OpenWindow>? WindowClick;

    /// <summary>As listas são as mesmas em todas as barras: páginas do Terminal e janelas de outros programas.</summary>
    internal void Bind(IEnumerable tabs, IEnumerable windows)
    {
        TabsList.ItemsSource = tabs;
        WindowsList.ItemsSource = windows;
    }

    /// <summary>A tela inicial só existe no monitor principal.</summary>
    internal void ShowHome(bool show) => HomeButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    internal void SetLocked(bool locked)
    {
        var visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        FilesButton.Visibility = visibility;
        PowerButton.Visibility = visibility;
    }

    internal void SetClock(string text) => ClockText.Text = text;

    internal void SetAttendant(string name)
    {
        AttendantInitials.Text = Brand.Initials(name);
        AttendantAvatar.Background = Brand.ColorFor(name);
        AttendantAvatar.ToolTip = name;
    }

    internal void SetHelpEnabled(bool enabled) => HelpButton.IsEnabled = enabled;

    private void OnHome(object sender, RoutedEventArgs e) => HomeClick?.Invoke();

    private void OnHelp(object sender, RoutedEventArgs e) => HelpClick?.Invoke();

    private void OnFiles(object sender, RoutedEventArgs e) => FilesClick?.Invoke();

    private void OnPower(object sender, RoutedEventArgs e) => PowerClick?.Invoke();

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SiteTab tab)
        {
            TabClick?.Invoke(tab);
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SiteTab tab)
        {
            TabCloseClick?.Invoke(tab);
        }
    }

    private void OnWindow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenWindow window)
        {
            WindowClick?.Invoke(window);
        }
    }
}
