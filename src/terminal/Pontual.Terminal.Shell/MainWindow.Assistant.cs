using System.IO;
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
        _whatsappTimer?.Stop();
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
        // "screen" avisa à página que este programa sabe entregar o que está na tela (as versões antigas não sabem);
        // "actions", que sabe abrir os sites e programas liberados quando a IA pede.
        var data = new { token = _enrollment.Token, hostname = Environment.MachineName, hosted = true, locked = _locked, screen = true, actions = true };
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
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            if (type.GetString() == "assistant-close")
            {
                _panel?.Hide();
                Activate();
            }
            else if (type.GetString() == "assistant-context"
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 40 } requestId)
            {
                // Páginas antigas não mandam "screen": para elas o pedido é sempre da tela.
                var wantsScreen = !root.TryGetProperty("screen", out var flag) || flag.ValueKind != JsonValueKind.False;
                _ = SendScreenAsync(requestId, wantsScreen);
            }
            else if (type.GetString() == "assistant-action"
                && root.TryGetProperty("id", out var actionId) && actionId.GetString() is { Length: > 0 and <= 40 } actionRequest
                && root.TryGetProperty("action", out var action) && action.GetString() is "open" or "search"
                && root.TryGetProperty("value", out var value) && value.GetString() is { Length: > 0 and <= 200 } target)
            {
                RunAssistantAction(actionRequest, action.GetString()!, target);
            }
        }
        catch (Exception ex)
        {
            Log.Error("mensagem da assistente", ex);
        }
    }

    // ------------------------------------------------------------- o que está na tela
    // Quando a atendente pergunta, a página da assistente pede o que está aberto: o texto e a foto da aba
    // em uso e a conversa aberta no WhatsApp. Tudo é lido na hora, vai junto com aquela pergunta e não é
    // guardado neste computador. A Central pode desligar isso (a página nem chega a pedir).

    private const int ScreenTimeoutMs = 2500;
    private const int ScreenImageWidth = 1400;
    private const int ScreenImageBytes = 380_000;

    /// <summary>
    /// Roda dentro da página do site e devolve { address, title, whatsapp, contact, text }. Só lê: não clica,
    /// não digita, não envia nada. Campo de senha nunca é lido. No WhatsApp devolve as últimas mensagens da
    /// conversa aberta; nos outros sites, o texto visível e os campos preenchidos.
    /// </summary>
    private const string ScreenScript =
        """
        (() => { try {
          const corta = (s, n, fim) => {
            s = String(s || '').replace(/[‎‏‪-‮]/g, '').replace(/[ \t]+/g, ' ').replace(/ ?\n ?/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
            return s.length <= n ? s : (fim ? s.slice(-n) : s.slice(0, n));
          };
          const saida = { address: location.host + location.pathname, title: corta(document.title, 200), whatsapp: /(^|\.)whatsapp\.com$/.test(location.hostname), contact: '', text: '' };
          if (saida.whatsapp) {
            // A área da conversa: #main, ou (se o WhatsApp mudou) o bloco que tem a caixa de escrever e um cabeçalho.
            let main = document.querySelector('#main') || document.querySelector('[data-testid="conversation-panel-wrapper"]');
            if (!main) {
              const caixa = document.querySelector('footer div[contenteditable="true"], [data-tab="10"][contenteditable="true"], div[aria-placeholder][contenteditable="true"]');
              for (let n = caixa; n && n !== document.body; n = n.parentElement) {
                if (n.querySelector('header') && n.querySelector('[data-pre-plain-text], [data-id^="true_"], [data-id^="false_"], [role="row"]')) { main = n; break; }
              }
            }
            if (!main) {
              const conta = (sel) => document.querySelectorAll(sel).length;
              saida.hint = 'main=' + conta('#main') + ' lista=' + conta('#pane-side') + ' header=' + conta('header') + ' footer=' + conta('footer')
                + ' caixa=' + conta('div[contenteditable="true"]') + ' msgs=' + conta('[data-pre-plain-text]') + ' ids=' + conta('[data-id^="true_"], [data-id^="false_"]')
                + ' app=' + conta('#app') + ' titulo=' + corta(document.title, 40);
              saida.text = 'Nenhuma conversa aberta no WhatsApp.'; return saida;
            }
            const cabecalho = main.querySelector('header');
            const nome = cabecalho ? cabecalho.querySelector('span[dir="auto"], span[title]') : null;
            saida.contact = corta(nome ? (nome.innerText || nome.getAttribute('title')) : cabecalho ? cabecalho.innerText.split('\n')[0] : '', 80);
            // Outros textos do cabeçalho: a cobertura confere o contato por qualquer um deles.
            saida.names = cabecalho ? [...cabecalho.querySelectorAll('span[title], span[dir="auto"]')]
              .map(s => corta(s.getAttribute('title') || s.innerText, 80)).filter(Boolean).slice(0, 8) : [];
            const linhas = [];
            for (const m of main.querySelectorAll('[data-pre-plain-text]')) {
              const hora = ((m.getAttribute('data-pre-plain-text') || '').match(/\[(\d{1,2}:\d{2})/) || [])[1] || '';
              const quem = m.closest('.message-out') ? 'Atendente' : 'Cliente';
              const texto = corta(m.innerText, 1500);
              if (texto) linhas.push('[' + hora + '] ' + quem + ': ' + texto);
            }
            // Outro jeito de achar as mensagens, se o WhatsApp mudou: cada uma tem data-id "true_..." (enviada) ou "false_..." (recebida).
            if (!linhas.length) for (const b of main.querySelectorAll('[data-id^="true_"], [data-id^="false_"]')) {
              const quem = (b.getAttribute('data-id') || '').startsWith('true_') ? 'Atendente' : 'Cliente';
              const bruto = corta(b.innerText, 1600);
              const horas = bruto.match(/\d{1,2}:\d{2}/g) || [];
              const sel = b.querySelector('.selectable-text');
              const texto = corta(sel ? sel.innerText : bruto.replace(/\s*\d{1,2}:\d{2}\s*$/, ''), 1500);
              if (texto) linhas.push('[' + (horas[horas.length - 1] || '') + '] ' + quem + ': ' + texto);
            }
            saida.text = linhas.length ? corta(linhas.slice(-40).join('\n'), 8000, true) : corta(main.innerText, 8000, true);
            return saida;
          }
          const partes = [corta(document.body ? document.body.innerText : '', 9000)];
          const documentos = [document];
          for (const quadro of document.querySelectorAll('iframe')) {
            try { const d = quadro.contentDocument; if (d && d.body) { documentos.push(d); partes.push(corta(d.body.innerText, 4000)); } } catch (e) { }
          }
          const campos = [];
          for (const d of documentos) for (const c of d.querySelectorAll('input, select, textarea')) {
            if (campos.length >= 60) break;
            const tipo = (c.type || '').toLowerCase();
            if (['password', 'hidden', 'submit', 'button', 'file', 'image', 'reset'].includes(tipo) || !c.offsetParent) continue;
            const valor = corta(c.tagName === 'SELECT' ? (c.selectedOptions[0] ? c.selectedOptions[0].text : '')
              : (tipo === 'checkbox' || tipo === 'radio') ? (c.checked ? 'marcado' : '') : c.value, 120);
            if (!valor) continue;
            let rotulo = '';
            if (c.id) { const l = d.querySelector('label[for="' + CSS.escape(c.id) + '"]'); if (l) rotulo = l.innerText; }
            campos.push(corta(rotulo || c.getAttribute('aria-label') || c.placeholder || c.name || c.id || 'campo', 60) + ': ' + valor);
          }
          saida.text = corta(partes.filter(Boolean).join('\n\n'), 12000) + (campos.length ? '\n\nCampos preenchidos:\n' + campos.join('\n') : '');
          return saida;
        } catch (e) { return null; } })()
        """;

    private async Task SendScreenAsync(string requestId, bool wantsScreen = true)
    {
        object? screen = null;
        try
        {
            screen = wantsScreen ? await CollectScreenAsync() : null;
        }
        catch (Exception ex)
        {
            // Sem a tela, a pergunta segue só com o que a atendente escreveu.
            Log.Error("leitura da tela para a assistente", ex);
        }

        try
        {
            _panel?.View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "assistant-context-result", id = requestId, screen, apps = AssistantApps() }, Json));
        }
        catch (Exception ex)
        {
            Log.Error("resposta à assistente", ex);
        }
    }

    // ------------------------------------------------------------- aprendizado com o WhatsApp
    // Quando a Central pede (no heartbeat), o Terminal registra a conversa de WhatsApp que está aberta: a cada
    // poucos segundos lê as últimas mensagens e, se algo mudou, manda ao servidor. Assim cada conversa que a
    // atendente abre entra na análise da assistente. Nada é guardado neste computador; no servidor, a conversa
    // é apagada depois de analisada.

    private static readonly TimeSpan WhatsAppEvery = TimeSpan.FromSeconds(8);
    private readonly Dictionary<string, string> _whatsappSent = [];
    private System.Windows.Threading.DispatcherTimer? _whatsappTimer;
    private bool _whatsappBusy;

    private void UpdateWhatsAppLearning(bool on)
    {
        if (!on)
        {
            _whatsappTimer?.Stop();
            _whatsappSent.Clear();
            return;
        }

        if (_whatsappTimer is null)
        {
            _whatsappTimer = new System.Windows.Threading.DispatcherTimer { Interval = WhatsAppEvery };
            _whatsappTimer.Tick += async (_, _) => await CaptureWhatsAppAsync();
        }

        _whatsappTimer.Start();
    }

    private async Task CaptureWhatsAppAsync()
    {
        if (_whatsappBusy || _api is not { } api || _state != HeartbeatResponse.StateActive)
        {
            return;
        }

        _whatsappBusy = true;
        try
        {
            foreach (var tab in _tabs.Where(t => !t.IsClosed && t.BlockedHost is null && IsWhatsApp(t)).Take(2).ToArray())
            {
                if (tab.View.CoreWebView2 is not { } core || await WithinAsync(core.ExecuteScriptAsync(ScreenScript)) is not { Length: > 0 } json)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("whatsapp", out var flag) || flag.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("contact", out var contactValue) || contactValue.GetString() is not { Length: > 0 } contact
                    || !root.TryGetProperty("text", out var textValue) || textValue.GetString() is not { Length: > 0 } text)
                {
                    continue;
                }

                // Só as mensagens já separadas ("[hora] Cliente: …"). Se a página do WhatsApp mudar e vier texto
                // solto, nada é mandado: emendar texto solto encheria a análise de repetições.
                if (!text.StartsWith('[') || (_whatsappSent.TryGetValue(contact, out var sent) && sent == text))
                {
                    continue;
                }

                await api.SendWhatsAppAsync(contact, text, _cts.Token);
                if (_whatsappSent.Count > 300)
                {
                    _whatsappSent.Clear();
                }

                _whatsappSent[contact] = text;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sem rede ou página fechada no meio da leitura: tenta de novo na próxima volta.
            Log.Error("registro do WhatsApp", ex);
        }
        finally
        {
            _whatsappBusy = false;
        }
    }

    private static bool IsWhatsApp(SiteTab tab) => tab.Target.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);

    private static async Task<T?> WithinAsync<T>(Task<T> task) where T : class =>
        await Task.WhenAny(task, Task.Delay(ScreenTimeoutMs)) == task ? await task : null;

    private async Task<object> CollectScreenAsync()
    {
        var active = _active is { IsClosed: false, BlockedHost: null } ? _active : null;
        // A aba em uso e, se o WhatsApp estiver aberto em outra aba, a conversa dele também.
        var tabs = new List<SiteTab>();
        if (active is not null)
        {
            tabs.Add(active);
        }

        tabs.AddRange(_tabs.Where(t => t != active && !t.IsClosed && t.BlockedHost is null && IsWhatsApp(t)).Take(1));

        var parts = new List<object>();
        foreach (var tab in tabs)
        {
            if (tab.View.CoreWebView2 is not { } core || await WithinAsync(core.ExecuteScriptAsync(ScreenScript)) is not { Length: > 0 } json)
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var whatsapp = root.TryGetProperty("whatsapp", out var flag) && flag.ValueKind == JsonValueKind.True;
            parts.Add(new
            {
                kind = whatsapp ? "whatsapp" : "page",
                active = tab == active,
                name = tab.Name,
                address = Field("address"),
                title = Field("title"),
                contact = Field("contact"),
                text = Field("text"),
            });
        }

        string? image = null;
        if (active?.View.CoreWebView2 is { } activeCore)
        {
            using var stream = new MemoryStream();
            var capture = activeCore.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            if (await Task.WhenAny(capture, Task.Delay(ScreenTimeoutMs)) == capture)
            {
                await capture;
                var bytes = stream.ToArray();
                image = Convert.ToBase64String(await Task.Run(() => ScreenCapture.Shrink(bytes, ScreenImageWidth, ScreenImageBytes)));
            }
        }

        if (active is null)
        {
            // Nenhum site em primeiro plano: a atendente está na tela inicial (ou num programa do Windows).
            var open = AppsInUse();
            parts.Insert(0, new
            {
                kind = "home",
                active = true,
                name = "Tela inicial",
                address = "",
                title = "",
                contact = "",
                text = open.Length > 0 ? "Tela inicial do Terminal. Abertos agora: " + string.Join(", ", open) + "." : "Tela inicial do Terminal, sem nenhum site ou programa aberto.",
            });
        }

        return new { parts, image };
    }
}
