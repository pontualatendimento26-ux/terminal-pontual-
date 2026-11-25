using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Notas adesivas da Pontual (pedido do dono, 25/11/2026): como as Notas Autoadesivas do Windows, que na conta
/// bloqueada não abrem. Cada nota é uma janelinha própria, colorida, que a atendente arrasta, redimensiona e deixa
/// na tela enquanto trabalha; a lista de notas é outra janela. O texto e a tela são a página <c>notas.html</c> da
/// Central (mudanças chegam sem atualizar o programa); aqui ficam só as janelas. As páginas das notas não recebem o
/// token: elas gravam no armazenamento do navegador interno e a tela inicial é quem envia para a Central.
/// </summary>
public partial class MainWindow
{
    /// <summary>Teto de janelas de nota abertas ao mesmo tempo (o mesmo teto de notas por computador da Central).</summary>
    private const int MaxNoteWindows = 50;

    private readonly Dictionary<string, NoteWindow> _notes = new(StringComparer.OrdinalIgnoreCase);
    private CalculatorWindow? _notesList;

    private string NotesUrl => _homeOrigin + (_local ? "/notas.html" : "/terminal/notas.html");

    /// <summary>
    /// Pedidos das páginas (tela inicial, lista e janelas das notas). Verdadeiro se a mensagem era das notas.
    /// <paramref name="sender"/> é a janela da nota que pediu, quando foi uma delas.
    /// </summary>
    private bool HandleNoteMessage(JsonElement root, string? type, NoteWindow? sender)
    {
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        double Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
        bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        // O id vira parte do endereço da janela: só passa se for mesmo um identificador de nota.
        var id = Guid.TryParse(Text("id"), out var parsed) ? parsed.ToString() : null;
        switch (type)
        {
            case "notes":
                _ = ShowNotesListAsync();
                return true;
            case "notes-close":
                _notesList?.Hide();
                return true;
            case "note-show" when id is not null:
                _ = ShowNoteAsync(id, Text("color"), new Rect(Number("x"), Number("y"), Math.Max(0, Number("w")), Math.Max(0, Number("h"))), Flag("activate"), Flag("near") ? sender : null);
                return true;
            case "note-close" when id is not null:
                CloseNote(id);
                return true;
            case "note-color" when id is not null:
                if (_notes.TryGetValue(id, out var window))
                {
                    window.SetColor(Text("color"));
                }

                return true;
            default:
                return false;
        }
    }

    private async Task ShowNoteAsync(string id, string? color, Rect saved, bool activate, NoteWindow? near)
    {
        if (_environment is null)
        {
            return;
        }

        if (_notes.TryGetValue(id, out var existing))
        {
            if (activate)
            {
                existing.Activate();
                existing.View.Focus();
            }

            return;
        }

        if (_notes.Count >= MaxNoteWindows)
        {
            return;
        }

        var window = new NoteWindow(color) { Owner = this, ShowActivated = activate };
        var placed = PlaceNote(window, saved, near);
        _notes[id] = window;

        void Post(object message)
        {
            try
            {
                window.View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
            {
                Log.Error("mensagem para a nota", ex);
            }
        }

        void PostGeometry() => Post(new
        {
            type = "geometry",
            x = (int)Math.Round(window.Left), y = (int)Math.Round(window.Top),
            w = (int)Math.Round(window.Width), h = (int)Math.Round(window.Height),
        });

        window.NewRequested += () => Post(new { type = "new" });
        window.MenuRequested += () => Post(new { type = "menu" });
        window.CloseRequested += () =>
        {
            // A página grava o que falta e pede para fechar; se ela não responder, a janela fecha assim mesmo.
            Post(new { type = "close" });
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                if (_notes.TryGetValue(id, out var current) && ReferenceEquals(current, window))
                {
                    CloseNote(id);
                }
            };
            fallback.Start();
        };
        window.GeometryChanged += PostGeometry;
        window.Show();

        try
        {
            await window.View.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            Log.Error("abrir a nota", ex);
            CloseNote(id);
            return;
        }

        if (!_notes.TryGetValue(id, out var still) || !ReferenceEquals(still, window))
        {
            return; // fechada enquanto abria
        }

        var core = window.View.CoreWebView2;
        await PrepareNotePageAsync(core, frame: true);
        core.WebMessageReceived += (_, e) => OnNoteMessage(e, window);
        var first = true;
        core.NavigationCompleted += (_, _) =>
        {
            // Nota nova (ou que estava fora da tela): o lugar escolhido aqui passa a ser o guardado.
            if (first && placed)
            {
                PostGeometry();
            }

            first = false;
            window.Track();
        };
        core.Navigate(NotesUrl + "?id=" + Uri.EscapeDataString(id) + (activate ? "" : "&foco=0"));
        if (activate)
        {
            window.View.Focus();
        }
    }

    /// <summary>
    /// Põe a janela no lugar guardado, se ele ainda cabe na tela; senão escolhe um (ao lado da nota que pediu, ou em
    /// escadinha no canto superior direito). Verdadeiro se o lugar foi escolhido aqui.
    /// </summary>
    private bool PlaceNote(NoteWindow window, Rect saved, NoteWindow? near)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        if (saved.Width >= window.MinWidth && saved.Height >= window.MinHeight)
        {
            // Precisa sobrar um pedaço do topo da nota dentro da tela, para dar para pegar e arrastar.
            var grip = new Rect(saved.Left, saved.Top, saved.Width, 32);
            grip.Intersect(screen);
            if (!grip.IsEmpty && grip.Width >= 60 && grip.Height >= 20)
            {
                window.Left = saved.Left;
                window.Top = saved.Top;
                window.Width = Math.Min(saved.Width, screen.Width);
                window.Height = Math.Min(saved.Height, screen.Height);
                return false;
            }
        }

        var area = SystemParameters.WorkArea;
        if (near is not null)
        {
            window.Left = near.Left + 36;
            window.Top = near.Top + 36;
        }
        else
        {
            var step = _notes.Count % 8 * 30;
            window.Left = area.Right - window.Width - 28 - step;
            window.Top = area.Top + 28 + step;
        }

        window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
        window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
        return true;
    }

    private async Task ShowNotesListAsync()
    {
        if (_environment is null)
        {
            return;
        }

        if (_notesList is not null)
        {
            if (!_notesList.IsVisible)
            {
                _notesList.Show();
            }

            if (_notesList.WindowState == WindowState.Minimized)
            {
                _notesList.WindowState = WindowState.Normal;
            }

            _notesList.Activate();
            _notesList.View.Focus();
            return;
        }

        var list = new CalculatorWindow { Owner = this, Title = "Notas Adesivas", Width = 380, Height = 620, MinWidth = 300, MinHeight = 360 };
        _notesList = list;
        var area = SystemParameters.WorkArea;
        list.WindowStartupLocation = WindowStartupLocation.Manual;
        list.Left = Math.Max(area.Left, area.Left + (area.Width - list.Width) / 2);
        list.Top = Math.Max(area.Top, area.Top + (area.Height - list.Height) / 2);
        list.Show();

        try
        {
            await list.View.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException)
        {
            Log.Error("abrir a lista de notas", ex);
            list.ForceClose();
            _notesList = null;
            ShowToast("Não foi possível abrir as Notas. Chame o TI.");
            return;
        }

        var core = list.View.CoreWebView2;
        await PrepareNotePageAsync(core, frame: false);
        core.WebMessageReceived += (_, e) => OnNoteMessage(e, null);
        core.Navigate(NotesUrl);
        list.View.Focus();
    }

    /// <summary>Trava a página das notas no próprio endereço e avisa que ela está dentro do programa. Sem o token.</summary>
    private async Task PrepareNotePageAsync(CoreWebView2 core, bool frame)
    {
        ApplySettings(core, isHome: true);
        var data = new { hosted = true, local = _local, locked = _locked, hostname = Environment.MachineName, notes = true, frame };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "(function(){if(window.top!==window||location.origin!==" + JsonSerializer.Serialize(_homeOrigin) + ")return;" +
            "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + JsonSerializer.Serialize(data, Json) + "),writable:false,configurable:false});})();");
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            if (!IsHomeUrl(args.Uri))
            {
                args.Cancel = true;
            }
        };
    }

    private void OnNoteMessage(CoreWebView2WebMessageReceivedEventArgs e, NoteWindow? sender)
    {
        try
        {
            if (!IsHomeUrl(e.Source))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                HandleNoteMessage(root, type.GetString(), sender);
            }
        }
        catch (Exception ex)
        {
            Log.Error("mensagem das notas", ex);
        }
    }

    private void CloseNote(string id)
    {
        if (_notes.Remove(id, out var window))
        {
            window.ForceClose();
        }
    }

    private void CloseNotes()
    {
        foreach (var window in _notes.Values.ToList())
        {
            window.ForceClose();
        }

        _notes.Clear();
        _notesList?.ForceClose();
        _notesList = null;
    }
}

/// <summary>
/// A janela de uma nota: sem a barra de título do Windows, com um topo da cor da nota (por onde se arrasta) e os
/// botões de nova nota, menu e fechar. O texto fica na página, dentro do WebView2. A borda de 4 pontos em volta é da
/// janela (e não da página) para o Windows conseguir redimensionar pelas beiradas.
/// </summary>
internal sealed class NoteWindow : Window
{
    private static readonly Dictionary<string, (string Body, string Top, bool Dark)> Palette = new(StringComparer.Ordinal)
    {
        ["yellow"] = ("#FFF7D1", "#FFF2AB", false),
        ["green"] = ("#E4F9E0", "#CBF1C4", false),
        ["pink"] = ("#FFE4F1", "#FFCCE5", false),
        ["purple"] = ("#F2E6FF", "#E7CFFF", false),
        ["blue"] = ("#E2F1FF", "#CDE9FF", false),
        ["gray"] = ("#F3F2F1", "#E1DFDD", false),
        ["charcoal"] = ("#696969", "#494745", true),
    };

    private readonly Border _frame = new() { BorderThickness = new Thickness(1) };
    private readonly Border _header = new() { Height = 32 };
    private readonly List<Button> _buttons = [];
    private readonly DispatcherTimer _geometryTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Brush _hover = Brushes.Transparent;
    private bool _allowClose;
    private bool _tracking;

    public NoteWindow(string? color)
    {
        Title = "Nota";
        Width = 300;
        Height = 300;
        MinWidth = 220;
        MinHeight = 170;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var bar = new DockPanel { LastChildFill = false };
        var add = HeaderButton("", "Nova nota", () => NewRequested?.Invoke());
        var close = HeaderButton("", "Fechar nota", () => CloseRequested?.Invoke());
        var menu = HeaderButton("", "Menu da nota", () => MenuRequested?.Invoke());
        DockPanel.SetDock(add, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(menu, Dock.Right);
        bar.Children.Add(add);
        bar.Children.Add(close);
        bar.Children.Add(menu);
        _header.Child = bar;
        _header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // O botão já foi solto: não há o que arrastar.
            }
        };

        View.Margin = new Thickness(4, 0, 4, 4);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_header, 0);
        Grid.SetRow(View, 1);
        grid.Children.Add(_header);
        grid.Children.Add(View);
        _frame.Child = grid;
        Content = _frame;
        SetColor(color);

        _geometryTimer.Tick += (_, _) =>
        {
            _geometryTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                GeometryChanged?.Invoke();
            }
        };
        LocationChanged += (_, _) => Moved();
        SizeChanged += (_, _) => Moved();
    }

    public WebView2 View { get; } = new();

    public event Action? NewRequested;
    public event Action? MenuRequested;
    public event Action? CloseRequested;

    /// <summary>A atendente moveu ou redimensionou a nota (avisado meio segundo depois de parar).</summary>
    public event Action? GeometryChanged;

    /// <summary>Passa a avisar as mudanças de lugar e de tamanho; antes de a página carregar não há quem ouça.</summary>
    public void Track() => _tracking = true;

    public void SetColor(string? color)
    {
        var (body, top, dark) = Palette.TryGetValue(color ?? "", out var found) ? found : Palette["yellow"];
        var bodyBrush = Frozen(body);
        Background = bodyBrush;
        _frame.Background = bodyBrush;
        _frame.BorderBrush = Frozen(dark ? "#33000000" : "#40000000");
        _header.Background = Frozen(top);
        _hover = Frozen(dark ? "#2EFFFFFF" : "#1A000000");
        var ink = dark ? Brushes.White : Frozen("#1F1F1F");
        foreach (var button in _buttons)
        {
            button.Foreground = ink;
        }
    }

    /// <summary>Fecha de verdade; o Alt+F4 passa antes pela página, para ela gravar o que falta.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        _geometryTimer.Stop();
        Close();
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Moved()
    {
        if (_tracking)
        {
            _geometryTimer.Stop();
            _geometryTimer.Start();
        }
    }

    private Button HeaderButton(string glyph, string label, Action action)
    {
        // Modelo próprio: o botão padrão do Windows pinta o fundo de azul ao passar o mouse.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var button = new Button
        {
            Template = template,
            Width = 36,
            Height = 32,
            Background = Brushes.Transparent,
            Focusable = false,
            ToolTip = label,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 },
        };
        AutomationProperties.SetName(button, label);
        button.MouseEnter += (_, _) => button.Background = _hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => action();
        _buttons.Add(button);
        return button;
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
            CloseRequested?.Invoke();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        View.Dispose();
        base.OnClosed(e);
    }
}
