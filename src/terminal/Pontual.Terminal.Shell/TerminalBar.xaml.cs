using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pontual.Terminal.Ipc;

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

    internal event Action? UpdateClick;

    internal event Action<SiteTab>? TabClick;

    internal event Action<SiteTab>? TabCloseClick;

    internal event Action<OpenWindow>? WindowClick;

    /// <summary>Item escolhido na pesquisa. Quem abre (e decide se pode abrir) é a <see cref="MainWindow"/>.</summary>
    internal event Action<ConfigItem>? SearchPick;

    /// <summary>De onde a pesquisa tira os itens: só o que está liberado para esta máquina agora.</summary>
    internal Func<IReadOnlyList<ConfigItem>>? SearchItems { get; set; }

    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;

    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

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

    /// <summary>O botão Atualizar só aparece no modo bloqueado e cadastrado numa Central (é de lá que vem a versão nova).</summary>
    internal void ShowUpdate(bool show) => UpdateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

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

    private void OnUpdate(object sender, RoutedEventArgs e) => UpdateClick?.Invoke();

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

    // ------------------------------------------------------------------ pesquisa

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        Filter(string.Empty);
        SearchPopup.IsOpen = true;
    }

    private void OnSearchOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Keyboard.Focus(SearchBox), System.Windows.Threading.DispatcherPriority.Input);

    private void OnSearchText(object sender, TextChangedEventArgs e) => Filter(SearchBox.Text);

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Pick(SearchResults.SelectedItem as SearchHit ?? SearchResults.Items.OfType<SearchHit>().FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Down when SearchResults.Items.Count > 0:
                SearchResults.SelectedIndex = Math.Max(SearchResults.SelectedIndex, 0);
                (SearchResults.ItemContainerGenerator.ContainerFromIndex(SearchResults.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Pick(SearchResults.SelectedItem as SearchHit);
                e.Handled = true;
                break;
            case Key.Up when SearchResults.SelectedIndex <= 0:
                Keyboard.Focus(SearchBox);
                e.Handled = true;
                break;
            case Key.Escape:
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SearchResults, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: SearchHit hit })
        {
            Pick(hit);
        }
    }

    private void Pick(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        SearchPopup.IsOpen = false;
        SearchPick?.Invoke(hit.Item);
    }

    /// <summary>Sem acento e sem diferença de maiúscula; nomes que começam com o texto vêm primeiro.</summary>
    private void Filter(string text)
    {
        var query = text.Trim();
        var items = SearchItems?.Invoke() ?? [];
        var hits = items
            .Where(i => query.Length == 0
                || Compare.IndexOf(i.Name, query, Loose) >= 0
                || Compare.IndexOf(i.Group ?? string.Empty, query, Loose) >= 0)
            .OrderBy(i => query.Length > 0 && Compare.IsPrefix(i.Name, query, Loose) ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true))
            .Select(i => new SearchHit(i))
            .ToArray();

        SearchResults.ItemsSource = hits;
        SearchResults.SelectedIndex = hits.Length > 0 ? 0 : -1;
        SearchResults.Visibility = hits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmpty.Text = items.Count == 0
            ? "Nenhum site ou programa liberado neste computador agora."
            : $"Nada encontrado para \"{query}\".";
        SearchEmpty.Visibility = hits.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Uma linha do resultado da pesquisa.</summary>
    private sealed class SearchHit(ConfigItem item)
    {
        public ConfigItem Item { get; } = item;

        public string Name => Item.Name;

        public string Initials { get; } = Brand.Initials(item.Name);

        public Brush Color { get; } = Brand.ColorFor(item.Name);

        public string Detail => string.IsNullOrWhiteSpace(Item.Group)
            ? (Item.IsProgram ? "Programa" : "Site")
            : $"{(Item.IsProgram ? "Programa" : "Site")} · {Item.Group}";
    }
}
