using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Calculadora do Terminal numa janela própria (pedido do dono, 21/11/2026): solta, para mover para o canto, mudar de
/// tamanho e continuar na tela quando a atendente troca de aba, como a Calculadora do Windows. A conta em si é a mesma
/// página da tela inicial (<c>app.js</c>) aberta em modo calculadora (<c>?calc=1</c>), então o visual e as teclas são
/// os de sempre e mudanças na Central chegam sem atualizar o programa. Fechar só esconde: a conta não se perde.
/// </summary>
public partial class MainWindow
{
    private CalculatorWindow? _calculator;

    private async Task ShowCalculatorAsync()
    {
        if (_environment is null)
        {
            return;
        }

        if (_calculator is not null)
        {
            if (!_calculator.IsVisible)
            {
                _calculator.Show();
            }

            if (_calculator.WindowState == WindowState.Minimized)
            {
                _calculator.WindowState = WindowState.Normal;
            }

            _calculator.Activate();
            _calculator.View.Focus();
            return;
        }

        _calculator = new CalculatorWindow { Owner = this };
        PlaceCalculator(_calculator);
        _calculator.Show();

        try
        {
            await _calculator.View.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException)
        {
            Log.Error("abrir a calculadora", ex);
            _calculator.ForceClose();
            _calculator = null;
            ShowToast("Não foi possível abrir a Calculadora. Chame o TI.");
            return;
        }

        var core = _calculator.View.CoreWebView2;
        ApplySettings(core, isHome: true);

        // A página só precisa saber que está dentro do programa, para pedir para fechar com Esc. Sem o token: não é a tela inicial.
        var data = new { hosted = true, local = _local, locked = _locked, hostname = Environment.MachineName, calc = true };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "(function(){if(window.top!==window||location.origin!==" + JsonSerializer.Serialize(_homeOrigin) + ")return;" +
            "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + JsonSerializer.Serialize(data, Json) + "),writable:false,configurable:false});})();");

        core.WebMessageReceived += OnCalculatorMessage;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            // A janela da calculadora nunca sai da própria página.
            if (!IsHomeUrl(args.Uri))
            {
                args.Cancel = true;
            }
        };
        core.Navigate(_homeUrl + "?calc=1");
        _calculator.View.Focus();
    }

    /// <summary>Primeira abertura: no canto inferior direito da área de trabalho, acima da barra do Terminal, como um aplicativo comum.</summary>
    private void PlaceCalculator(CalculatorWindow window)
    {
        var area = SystemParameters.WorkArea;
        const double margin = 24;
        var bar = Bar.ActualHeight > 0 ? Bar.ActualHeight : 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Max(area.Left, area.Right - window.Width - margin);
        window.Top = Math.Max(area.Top, area.Bottom - window.Height - margin - bar);
    }

    private void OnCalculatorMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "calc-close")
            {
                _calculator?.Hide();
                Activate();
            }
        }
        catch (Exception ex)
        {
            Log.Error("mensagem da calculadora", ex);
        }
    }

    private void CloseCalculator()
    {
        _calculator?.ForceClose();
        _calculator = null;
    }
}

/// <summary>Janela comum do Windows (barra de título, mover, redimensionar) com a calculadora dentro. Fechar só esconde.</summary>
internal sealed class CalculatorWindow : Window
{
    private bool _allowClose;

    public CalculatorWindow()
    {
        Title = "Calculadora";
        Width = 380;
        Height = 600;
        MinWidth = 300;
        MinHeight = 440;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        Content = View;
    }

    public WebView2 View { get; } = new();

    /// <summary>Fecha de verdade (quando o Terminal fecha); o X e o Alt+F4 só escondem, para a conta não se perder.</summary>
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
            Owner?.Activate();
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
