using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pontual.Terminal.Shell;

// ------------------------------------------------------------- botão Atualizar
// A atendente não é administradora, então o Terminal não troca os próprios arquivos. Ele só:
//   1. consulta na Central se há versão nova (/baixar/atualizacao/versao.json) e pede confirmação;
//   2. deixa o endereço da Central em ProgramData\Pontual\Atualizacao\pedido.txt;
//   3. dispara a tarefa do Windows "Terminal Pontual - Atualizar", que roda como SISTEMA o
//      Atualizar-Terminal.ps1 da pasta do programa (confere a assinatura, troca os arquivos e reinicia);
//   4. mostra o andamento que a tarefa escreve em estado.txt.
// A tarefa e a pasta são criadas pelo instalador; nada disso existe nos modos sem bloqueio.
public partial class MainWindow
{
    private const string UpdateTask = "Terminal Pontual - Atualizar";
    private static readonly string UpdateFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pontual", "Atualizacao");
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private bool _updating;

    internal static Version CurrentVersion
    {
        get
        {
            var text = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return Version.TryParse(text.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
        }
    }

    private async void StartUpdate(Window owner)
    {
        if (_updating || _local)
        {
            return;
        }

        _updating = true;
        try
        {
            HideToast();
            var central = new Uri(_homeOrigin).GetLeftPart(UriPartial.Authority);
            var current = CurrentVersion;

            Version? latest;
            try
            {
                latest = await LatestVersionAsync(central);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                Log.Error("atualização: consulta", ex);
                UpdateWindow.Message(owner, "Não foi possível consultar a Central", "Confira a conexão e tente de novo. Se continuar, chame o TI.");
                return;
            }

            if (latest is null)
            {
                UpdateWindow.Message(owner, "Nenhuma atualização publicada", "A Central do TI ainda não tem uma versão do Terminal para instalar por aqui.");
                return;
            }

            if (latest <= current)
            {
                UpdateWindow.Message(owner, "O Terminal já está atualizado", $"Este computador já está na versão mais recente ({Short(current)}).");
                return;
            }

            if (!Directory.Exists(UpdateFolder))
            {
                UpdateWindow.Message(owner, "Atualização não configurada",
                    "Este computador ainda não tem o botão Atualizar configurado. O TI precisa instalar esta versão uma vez pelo instalador; depois, as próximas vêm por aqui.");
                return;
            }

            if (!UpdateWindow.Confirm(owner, Short(current), Short(latest)))
            {
                return;
            }

            try
            {
                File.Delete(Path.Combine(UpdateFolder, "estado.txt"));
                File.WriteAllText(Path.Combine(UpdateFolder, "pedido.txt"), central, new UTF8Encoding(false));
                RunUpdateTask();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                Log.Error("atualização: início", ex);
                UpdateWindow.Message(owner, "Não foi possível começar a atualização", "Chame o TI. Detalhe: " + ex.Message);
                return;
            }

            Log.Info($"atualização pedida: {Short(current)} -> {Short(latest)}");
            UpdateWindow.Progress(owner, Path.Combine(UpdateFolder, "estado.txt"));
        }
        finally
        {
            _updating = false;
        }
    }

    // ------------------------------------------------------------- aviso de versão nova
    // A cada 30 minutos o Terminal confere se a Central publicou versão nova. Havendo, o botão Atualizar fica
    // verde com a versão e aparece um aviso uma vez. Só a atendente instala, clicando no botão.
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromMinutes(30);
    private DateTime _updateCheckedAt = DateTime.MinValue;
    private Version? _updateAnnounced;

    private async Task CheckForUpdateAsync()
    {
        var now = DateTime.UtcNow;
        if (_updating || _local || !_locked || now - _updateCheckedAt < UpdateCheckEvery)
        {
            return;
        }

        _updateCheckedAt = now;
        try
        {
            var latest = await LatestVersionAsync(new Uri(_homeOrigin).GetLeftPart(UriPartial.Authority));
            var available = latest is not null && latest > CurrentVersion ? latest : null;
            foreach (var bar in Bars)
            {
                bar.SetUpdateAvailable(available is null ? null : Short(available));
            }

            if (available is not null && available != _updateAnnounced)
            {
                _updateAnnounced = available;
                ShowToast($"Tem uma versão nova do Terminal ({Short(available)}). Clique em \"Atualizar\", no alto da tela, quando puder.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            // Sem rede: confere de novo na próxima volta.
            Log.Error("atualização: consulta automática", ex);
        }
    }

    private static string Short(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    private static async Task<Version?> LatestVersionAsync(string central)
    {
        using var response = await UpdateHttp.GetAsync(central + "/baixar/atualizacao/versao.json");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Version.Parse(doc.RootElement.GetProperty("versao").GetString() ?? "");
    }

    /// <summary>Dispara a tarefa pelo schtasks do Windows, iniciado direto (sem passar pelo shell).</summary>
    private static void RunUpdateTask()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        using var process = Process.Start(new ProcessStartInfo(exe, $"/run /tn \"{UpdateTask}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("O Windows não abriu o agendador de tarefas.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"código {process.ExitCode}" : error.Trim());
        }
    }
}

/// <summary>Janelas do botão Atualizar: aviso simples, confirmação e andamento.</summary>
internal sealed class UpdateWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly StackPanel _buttons;

    private UpdateWindow(Window owner, string title, string text)
    {
        Owner = owner;
        Title = "Terminal Pontual";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("FonteMarca");
        FontSize = 15;

        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        _title = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Tinta"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        _text = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Suave"),
            Margin = new Thickness(0, 0, 0, 18),
        };
        _buttons = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(_text);
        panel.Children.Add(_buttons);
        Content = panel;
    }

    private Button AddButton(string label, string style, bool result)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)Application.Current.FindResource(style),
            Margin = new Thickness(0, 0, 0, 8),
        };
        button.Click += (_, _) => DialogResult = result;
        _buttons.Children.Add(button);
        return button;
    }

    public static void Message(Window owner, string title, string text)
    {
        var window = new UpdateWindow(owner, title, text);
        window.AddButton("Entendi", "BotaoPilula", true).IsCancel = true;
        window.ShowDialog();
    }

    public static bool Confirm(Window owner, string current, string latest)
    {
        var window = new UpdateWindow(owner, $"Atualizar para a versão {latest}?",
            $"Versão instalada: {current}.\n\nO Terminal baixa a versão nova, troca a antiga e REINICIA o computador sozinho. " +
            "Salve o que estiver fazendo: as páginas abertas serão fechadas. Leva poucos minutos.");
        window.AddButton("Atualizar e reiniciar", "BotaoPilula", true);
        window.AddButton("Agora não", "BotaoClaro", false).IsCancel = true;
        return window.ShowDialog() == true;
    }

    /// <summary>Acompanha o estado.txt escrito pela tarefa até ela terminar, dar erro ou o computador reiniciar.</summary>
    public static void Progress(Window owner, string stateFile)
    {
        var window = new UpdateWindow(owner, "Atualizando o Terminal", "Começando...");
        var close = window.AddButton("Fechar", "BotaoClaro", false);
        close.Visibility = Visibility.Collapsed;
        // Enquanto a tarefa trabalha a janela não fecha (nem por Alt+F4): a atendente vê o que está acontecendo.
        window.Closing += (_, e) => e.Cancel = close.Visibility != Visibility.Visible;

        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var (step, text) = ReadState(stateFile);
            if (text.Length > 0)
            {
                window._text.Text = text;
            }

            var finished = step is "erro" or "atual";
            if (step == "erro")
            {
                window._title.Text = "A atualização não foi concluída";
            }
            else if (step == "atual")
            {
                window._title.Text = "O Terminal já está atualizado";
            }
            else if (step.Length == 0 && DateTime.UtcNow - started > TimeSpan.FromMinutes(2))
            {
                window._title.Text = "A atualização não começou";
                window._text.Text = "O Windows não iniciou a atualização. Chame o TI.";
                finished = true;
            }
            else if (DateTime.UtcNow - started > TimeSpan.FromMinutes(30))
            {
                window._title.Text = "A atualização está demorando";
                window._text.Text = "Chame o TI.";
                finished = true;
            }

            if (finished)
            {
                timer.Stop();
                close.Visibility = Visibility.Visible;
            }
        };
        timer.Start();
        window.ShowDialog();
        timer.Stop();
    }

    private static (string Step, string Text) ReadState(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return ("", "");
            }

            var content = File.ReadAllText(file, Encoding.UTF8).Trim();
            var bar = content.IndexOf('|');
            return bar < 0 ? (content, "") : (content[..bar], content[(bar + 1)..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A tarefa pode estar escrevendo neste instante; lê de novo no próximo segundo.
            return ("", "");
        }
    }
}
