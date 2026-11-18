using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

// Assistente de IA: o botão flutuante e a janela da conversa.
// Só existe no modo com servidor, e só aparece quando o servidor diz (no heartbeat) que a assistente
// está ligada para esta máquina. A conversa é uma página do servidor; a chave da IA nunca chega aqui.
public partial class MainWindow
{
    private const double AssistantMargin = 16;

    private bool _assistantEnabled;
    private bool _assistantOpening;
    private AssistantBubble? _bubble;
    private AssistantPanel? _panel;

    private string AssistantUrl => _homeOrigin + "/terminal/assistente.html";

    /// <summary>Mostra ou esconde o botão conforme o estado da máquina. Chamado a cada mudança de tela.</summary>
    private void UpdateAssistant()
    {
        var show = !_local && _assistantEnabled && !_unenrolled && _fatalError is null
            && _state == HeartbeatResponse.StateActive && _environment is not null && IsLoaded;

        if (!show)
        {
            _panel?.Hide();
            _bubble?.Hide();
            return;
        }

        if (_bubble is null)
        {
            _bubble = new AssistantBubble { Owner = this };
            _bubble.Clicked += (_, _) => ToggleAssistant();
            _bubble.Moved += (_, _) => PlaceAssistantPanel();
        }

        if (!_bubble.IsVisible)
        {
            PlaceAssistantBubble();
            _bubble.Show();
        }
    }

    private void CloseAssistant()
    {
        _panel?.ForceClose();
        _panel = null;
        _bubble?.Close();
        _bubble = null;
    }

    /// <summary>Área do conteúdo da janela principal (acima da barra do rodapé), em unidades do WPF na tela.</summary>
    private Rect AssistantArea()
    {
        var origin = ContentHost.PointToScreen(new Point(0, 0));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            origin = target.TransformFromDevice.Transform(origin);
        }

        return new Rect(origin.X, origin.Y, Math.Max(ContentHost.ActualWidth, AssistantBubble.Side), Math.Max(ContentHost.ActualHeight, AssistantBubble.Side));
    }

    /// <summary>Canto inferior direito do conteúdo, como os botões de ajuda dos sites.</summary>
    private void PlaceAssistantBubble()
    {
        if (_bubble is null)
        {
            return;
        }

        var area = AssistantArea();
        _bubble.Left = area.Right - AssistantBubble.Side - AssistantMargin;
        _bubble.Top = area.Bottom - AssistantBubble.Side - AssistantMargin;
    }

    /// <summary>A conversa abre colada ao botão: acima dele se couber, senão abaixo; sempre dentro da janela principal.</summary>
    private void PlaceAssistantPanel()
    {
        if (_panel is null || _bubble is null)
        {
            return;
        }

        var area = AssistantArea();
        var left = _bubble.Left + AssistantBubble.Side - AssistantPanel.PanelWidth;
        var top = _bubble.Top - AssistantPanel.PanelHeight;
        if (top < area.Top)
        {
            top = _bubble.Top + AssistantBubble.Side;
        }

        _panel.Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - AssistantPanel.PanelWidth));
        _panel.Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - AssistantPanel.PanelHeight));
    }

    private async void ToggleAssistant()
    {
        try
        {
            if (_panel is { IsVisible: true })
            {
                _panel.Hide();
                return;
            }

            await ShowAssistantPanelAsync();
        }
        catch (Exception ex)
        {
            // Manipulador "async void": nada pode escapar daqui.
            Log.Error("assistente", ex);
            _panel?.ForceClose();
            _panel = null;
            ShowToast("Não foi possível abrir a assistente. Chame o TI.");
        }
        finally
        {
            _assistantOpening = false;
        }
    }

    private async Task ShowAssistantPanelAsync()
    {
        if (_assistantOpening || _environment is null)
        {
            return;
        }

        _assistantOpening = true;

        if (_panel is not null)
        {
            PlaceAssistantPanel();
            _panel.Show();
            _panel.Activate();
            _panel.View.Focus();
            _panel.View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "assistant-shown" }, Json));
            return;
        }

        _panel = new AssistantPanel { Owner = this };
        PlaceAssistantPanel();
        _panel.Show();

        await _panel.View.EnsureCoreWebView2Async(_environment);
        var core = _panel.View.CoreWebView2;
        ApplySettings(core, isHome: true);

        // O mesmo objeto window.PONTUAL da tela inicial, com o token da máquina, e só para a página do próprio servidor.
        var data = new { token = _enrollment.Token, hostname = Environment.MachineName, hosted = true, locked = _locked };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "(function(){if(window.top!==window||location.origin!==" + JsonSerializer.Serialize(_homeOrigin) + ")return;" +
            "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + JsonSerializer.Serialize(data, Json) + "),writable:false,configurable:false});})();");

        core.WebMessageReceived += OnAssistantMessage;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            // A janela da assistente nunca sai da própria página.
            if (!IsAssistantUrl(args.Uri))
            {
                args.Cancel = true;
            }
        };
        core.Navigate(AssistantUrl);
        _panel.View.Focus();
    }

    private bool IsAssistantUrl(string? url) =>
        url is not null && (url.Equals(AssistantUrl, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(AssistantUrl + "?", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(AssistantUrl + "#", StringComparison.OrdinalIgnoreCase));

    private void OnAssistantMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsAssistantUrl(e.Source))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "assistant-close")
            {
                _panel?.Hide();
                Activate();
            }
        }
        catch (Exception ex)
        {
            Log.Error("mensagem da assistente", ex);
        }
    }
}
