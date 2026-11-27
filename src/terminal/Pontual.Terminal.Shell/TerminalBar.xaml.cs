using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>Botão de janela da aba: solta o site numa janela comum, ou devolve à tela inteira.</summary>
    internal event Action<SiteTab>? TabFloatClick;

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
        _tabs = tabs;
        _windows = windows;
        foreach (var tab in tabs.OfType<SiteTab>())
        {
            tab.PropertyChanged += OnTabChanged;
        }

        if (tabs is INotifyCollectionChanged tabsChanged)
        {
            tabsChanged.CollectionChanged += OnListsChanged;
        }

        if (windows is INotifyCollectionChanged windowsChanged)
        {
            windowsChanged.CollectionChanged += OnListsChanged;
        }

        UpdateAllTabsButton();
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

    /// <summary>Com versão nova publicada na Central, o botão fica verde e diz a versão; nada é instalado sem o clique.</summary>
    internal void SetUpdateAvailable(string? version)
    {
        UpdateButton.Style = (Style)FindResource(version is null ? "BotaoClaro" : "BotaoVerde");
        UpdateText.Text = version is null ? "Atualizar" : $"Atualizar para {version}";
        UpdateButton.ToolTip = version is null
            ? "Instala a versão nova do Terminal e reinicia o computador"
            : $"Tem uma versão nova do Terminal ({version}). Clique quando puder: o Terminal reinicia em cerca de 1 minuto.";
    }

    internal void SetClock(string text)
    {
        if (ClockText.Text == text)
        {
            return;
        }

        ClockText.Text = text;
        ShowDate();

        // Uma vez por minuto o ícone confere o som: o volume pode ter mudado pelo teclado ou pela barra do outro monitor.
        ShowVolumeIcon(SystemVolume.Read());
    }

    // ------------------------------------------------------------------ volume do som
    // O volume é do próprio computador, então a barra resolve sozinha, sem passar pela MainWindow.

    private bool _volumeSync;

    private void OnVolume(object sender, RoutedEventArgs e)
    {
        ShowVolume(SystemVolume.Read(), moveSlider: true);
        VolumePopup.IsOpen = true;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeSync)
        {
            return;
        }

        var percent = (int)Math.Round(e.NewValue);
        if (SystemVolume.SetPercent(percent))
        {
            // O controle fica onde a atendente soltou; só os textos acompanham. Com volume zero, o mudo fica como estava.
            var muted = percent == 0 && SystemVolume.Read() is { Muted: true };
            ShowVolume((percent, muted), moveSlider: false);
        }
        else
        {
            ShowVolume(SystemVolume.Read(), moveSlider: true);
        }
    }

    private void OnMute(object sender, RoutedEventArgs e)
    {
        if (SystemVolume.Read() is { } state)
        {
            SystemVolume.SetMuted(!state.Muted);
        }

        ShowVolume(SystemVolume.Read(), moveSlider: true);
    }

    private void ShowVolume((int Percent, bool Muted)? state, bool moveSlider)
    {
        _volumeSync = true;
        try
        {
            VolumeSlider.IsEnabled = state is not null;
            MuteButton.IsEnabled = state is not null;
            VolumeMissing.Visibility = state is null ? Visibility.Visible : Visibility.Collapsed;
            VolumePercent.Text = state is not { } found ? string.Empty : found.Muted ? "Sem som" : $"{found.Percent}%";
            MuteButton.Content = state is { Muted: true } ? "Ativar o som" : "Silenciar";
            if (moveSlider)
            {
                VolumeSlider.Value = state?.Percent ?? 0;
            }
        }
        finally
        {
            _volumeSync = false;
        }

        ShowVolumeIcon(state);
    }

    /// <summary>O alto-falante da barra ganha um X vermelho quando o som está silenciado ou no zero.</summary>
    private void ShowVolumeIcon((int Percent, bool Muted)? state)
    {
        var silent = state is { Muted: true } or { Percent: 0 };
        VolumeCross.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
        VolumeWaves.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
        VolumeButton.ToolTip = state is not { } found ? "Volume do som"
            : silent ? "Volume do som: sem som"
            : $"Volume do som: {found.Percent}%";
    }

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

    private void OnTabFloat(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SiteTab tab)
        {
            TabFloatClick?.Invoke(tab);
        }
    }

    private void OnWindow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenWindow window)
        {
            WindowClick?.Invoke(window);
        }
    }

    // ------------------------------------------------------------------ todas as abas

    private IEnumerable _tabs = Array.Empty<SiteTab>();
    private IEnumerable _windows = Array.Empty<OpenWindow>();

    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.OldItems?.OfType<SiteTab>() ?? [])
        {
            tab.PropertyChanged -= OnTabChanged;
        }

        foreach (var tab in e.NewItems?.OfType<SiteTab>() ?? [])
        {
            tab.PropertyChanged += OnTabChanged;
            if (tab.IsSelected)
            {
                ScrollToTab(tab);
            }
        }

        UpdateAllTabsButton();
        if (AllTabsPopup.IsOpen)
        {
            FilterAllTabs(AllTabsBox.Text);
        }
    }

    /// <summary>A aba que passa a aparecer na tela rola para dentro da faixa, mesmo que estivesse escondida no fim.</summary>
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteTab.IsSelected) && sender is SiteTab { IsSelected: true } tab)
        {
            ScrollToTab(tab);
        }
    }

    private void ScrollToTab(SiteTab tab) =>
        Dispatcher.BeginInvoke(() => (TabsList.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView(),
            System.Windows.Threading.DispatcherPriority.Loaded);

    private int OpenCount => _tabs.OfType<SiteTab>().Count() + _windows.OfType<OpenWindow>().Count();

    private void UpdateAllTabsButton()
    {
        var count = OpenCount;
        AllTabsButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllTabsText.Text = $"Abas ({count})";
        AllTabsButton.ToolTip = count == 1 ? "Ver a aba aberta" : $"Ver as {count} abas e janelas abertas";
        if (count == 0)
        {
            AllTabsPopup.IsOpen = false;
        }
    }

    /// <summary>A roda do mouse rola a faixa das abas para o lado.</summary>
    private void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        TabsStrip.ScrollToHorizontalOffset(TabsStrip.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Setas do carrossel: cada clique anda quase uma faixa inteira (fica um pedaço da última aba, para não se perder).</summary>
    private void OnStripLeft(object sender, RoutedEventArgs e) =>
        TabsStrip.ScrollToHorizontalOffset(Math.Max(0, TabsStrip.HorizontalOffset - StripStep));

    private void OnStripRight(object sender, RoutedEventArgs e) =>
        TabsStrip.ScrollToHorizontalOffset(Math.Min(TabsStrip.ScrollableWidth, TabsStrip.HorizontalOffset + StripStep));

    private double StripStep => Math.Max(120, TabsStrip.ViewportWidth * 0.8);

    /// <summary>Mostra cada seta só quando há abas escondidas daquele lado.</summary>
    private void OnStripScrolled(object sender, ScrollChangedEventArgs e)
    {
        var overflow = TabsStrip.ScrollableWidth > 1;
        StripLeft.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        StripRight.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        StripLeft.IsEnabled = TabsStrip.HorizontalOffset > 1;
        StripRight.IsEnabled = TabsStrip.HorizontalOffset < TabsStrip.ScrollableWidth - 1;
    }

    private void OnAllTabs(object sender, RoutedEventArgs e)
    {
        AllTabsBox.Text = string.Empty;
        FilterAllTabs(string.Empty);
        AllTabsPopup.IsOpen = true;
    }

    private void OnAllTabsOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Keyboard.Focus(AllTabsBox), System.Windows.Threading.DispatcherPriority.Input);

    private void OnAllTabsText(object sender, TextChangedEventArgs e) => FilterAllTabs(AllTabsBox.Text);

    private void OnAllTabsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                PickEntry(AllTabsResults.SelectedItem as OpenEntry ?? AllTabsResults.Items.OfType<OpenEntry>().FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Down when AllTabsResults.Items.Count > 0:
                AllTabsResults.SelectedIndex = Math.Max(AllTabsResults.SelectedIndex, 0);
                (AllTabsResults.ItemContainerGenerator.ContainerFromIndex(AllTabsResults.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                AllTabsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnAllTabsResultsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                PickEntry(AllTabsResults.SelectedItem as OpenEntry);
                e.Handled = true;
                break;
            case Key.Delete when AllTabsResults.SelectedItem is OpenEntry { Tab: { } tab }:
                TabCloseClick?.Invoke(tab);
                e.Handled = true;
                break;
            case Key.Up when AllTabsResults.SelectedIndex <= 0:
                Keyboard.Focus(AllTabsBox);
                e.Handled = true;
                break;
            case Key.Escape:
                AllTabsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnAllTabsPick(object sender, RoutedEventArgs e) => PickEntry((sender as FrameworkElement)?.DataContext as OpenEntry);

    /// <summary>Fechar pela lista: a lista continua aberta, para fechar várias de uma vez.</summary>
    private void OnAllTabsClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenEntry { Tab: { } tab })
        {
            TabCloseClick?.Invoke(tab);
        }
    }

    private void PickEntry(OpenEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        AllTabsPopup.IsOpen = false;
        if (entry.Tab is { } tab)
        {
            TabClick?.Invoke(tab);
        }
        else if (entry.Window is { } window)
        {
            WindowClick?.Invoke(window);
        }
    }

    /// <summary>Páginas do Terminal primeiro (na ordem em que foram abertas), depois as janelas dos outros programas.</summary>
    private void FilterAllTabs(string text)
    {
        var query = text.Trim();
        var all = _tabs.OfType<SiteTab>().Select(t => new OpenEntry(t))
            .Concat(_windows.OfType<OpenWindow>().Select(w => new OpenEntry(w)))
            .ToArray();
        var hits = all.Where(x => query.Length == 0 || Compare.IndexOf(x.Name, query, Loose) >= 0).ToArray();

        AllTabsTitle.Text = all.Length == 1 ? "1 aba aberta" : $"{all.Length} abas abertas";
        AllTabsResults.ItemsSource = hits;
        AllTabsResults.SelectedIndex = hits.Length > 0 ? 0 : -1;
        AllTabsResults.Visibility = hits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllTabsEmpty.Text = all.Length == 0 ? "Nenhuma aba aberta." : $"Nenhuma aba com \"{query}\".";
        AllTabsEmpty.Visibility = hits.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Uma linha da lista de todas as abas: uma página do Terminal ou a janela de outro programa.</summary>
    private sealed class OpenEntry
    {
        public OpenEntry(SiteTab tab)
        {
            Tab = tab;
            Name = tab.Name;
            Color = tab.Color;
            Initials = tab.Initials;
            Logo = tab.Logo;
            Detail = tab.IsFloating ? (tab.IsSelected ? "Página do Terminal · em janela" : "Página do Terminal · em janela (minimizada)")
                : tab.IsSelected ? "Página do Terminal · na tela agora"
                : tab.Monitor is not null ? "Página do Terminal · no outro monitor"
                : "Página do Terminal";
            Weight = tab.IsSelected ? FontWeights.SemiBold : FontWeights.Medium;
        }

        public OpenEntry(OpenWindow window)
        {
            Window = window;
            Name = string.IsNullOrWhiteSpace(window.Title) ? "Janela sem título" : window.Title;
            Logo = window.Logo;
            Color = Logo is null ? Brand.ColorFor(Name) : Brushes.White;
            Initials = Logo is null ? Brand.Initials(Name) : "";
            Detail = "Programa";
            Weight = FontWeights.Medium;
        }

        public SiteTab? Tab { get; }

        public OpenWindow? Window { get; }

        public string Name { get; }

        public ImageSource? Logo { get; }

        public string Initials { get; }

        public Brush Color { get; }

        public string Detail { get; }

        public FontWeight Weight { get; }

        /// <summary>Só as páginas do Terminal fecham por aqui; os outros programas se fecham neles mesmos.</summary>
        public Visibility CloseVisibility => Tab is null ? Visibility.Collapsed : Visibility.Visible;

        public string CloseLabel => $"Fechar {Name}";
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

        public ImageSource? Logo { get; } = Logos.For(item);

        public string Initials { get; } = Logos.For(item) is null ? Brand.Initials(item.Name) : "";

        public Brush Color { get; } = Logos.For(item) is null ? Brand.ColorFor(item.Name) : Brushes.White;

        public string Detail => string.IsNullOrWhiteSpace(Item.Group)
            ? (Item.IsProgram ? "Programa" : "Site")
            : $"{(Item.IsProgram ? "Programa" : "Site")} · {Item.Group}";
    }

    /// <summary>Aviso fixo da barra: com a gravação ligada, diz que a tela é gravada (em vermelho, para não passar despercebido).</summary>
    public void SetRecording(bool recording, string hours = "")
    {
        MonitorNotice.Text = recording ? $"● A tela e as atividades são gravadas{hours}" : "As atividades serão monitoradas";
        MonitorNotice.SetResourceReference(TextBlock.ForegroundProperty, recording ? "Rubi700" : "Apagado");
    }
}
