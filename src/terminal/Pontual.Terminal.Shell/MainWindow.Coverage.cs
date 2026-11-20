using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

// ------------------------------------------------------------- cobertura de ausência
// Quando a supervisão liga a cobertura de uma atendente que faltou, este Terminal (com o WhatsApp dela aberto)
// procura conversas não lidas, abre uma por vez, manda as mensagens ao servidor e envia a resposta que voltar.
// Também envia as respostas que a supervisão aprovou na Central. No modo ensaio só lê: nada é digitado.
// Cuidados: uma conversa por volta, intervalo entre envios, confere o nome do contato antes de digitar,
// e não mexe no WhatsApp enquanto alguém está usando este computador.
public partial class MainWindow
{
    private const string CoverageOn = "on";
    private static readonly TimeSpan CoverageEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CoverageEveryAbsent = TimeSpan.FromSeconds(10);
    /// <summary>Sem teclado nem mouse por este tempo, o computador está livre para a cobertura usar o WhatsApp.</summary>
    private static readonly TimeSpan CoverageIdle = TimeSpan.FromSeconds(90);
    /// <summary>No dia em que a atendente faltou, quase não espera: só não atropela quem mexeu no computador agora.</summary>
    private static readonly TimeSpan CoverageIdleAbsent = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CoverageRetry = TimeSpan.FromMinutes(1);

    private string _coverage = "";
    /// <summary>A supervisão marcou que a atendente faltou hoje: responde na hora.</summary>
    private bool _coverageAbsent;
    /// <summary>O que a cobertura está fazendo, em palavras simples; vai para a Central no sinal de vida.</summary>
    private string _coverageStatus = "";
    private System.Windows.Threading.DispatcherTimer? _coverageTimer;
    private bool _coverageBusy;
    /// <summary>Conversas já abertas (e por isso marcadas como lidas) que ainda esperam uma nova tentativa.</summary>
    private readonly Dictionary<string, DateTime> _coverageRetry = [];

    private void UpdateCoverage(string mode, bool absent = false)
    {
        if (absent != _coverageAbsent)
        {
            _coverageAbsent = absent;
            Log.Info($"cobertura: {(absent ? "atendente faltou hoje, responde na hora" : "falta desmarcada")}");
            if (_coverageTimer is not null)
            {
                _coverageTimer.Interval = absent ? CoverageEveryAbsent : CoverageEvery;
            }
        }

        if (mode == _coverage)
        {
            return;
        }

        _coverage = mode;
        Log.Info($"cobertura de ausência: {(mode.Length == 0 ? "desligada" : mode)}");
        if (mode.Length == 0)
        {
            _coverageTimer?.Stop();
            _coverageRetry.Clear();
            _coverageStatus = "";
            return;
        }

        if (_coverageTimer is null)
        {
            _coverageTimer = new System.Windows.Threading.DispatcherTimer { Interval = _coverageAbsent ? CoverageEveryAbsent : CoverageEvery };
            _coverageTimer.Tick += async (_, _) => await CoverAsync();
        }

        _coverageStatus = "Começando a olhar o WhatsApp.";
        _coverageTimer.Start();
    }

    private async Task CoverAsync()
    {
        if (_coverageBusy || _coverage.Length == 0 || _api is not { } api)
        {
            return;
        }

        if (_state != HeartbeatResponse.StateActive)
        {
            _coverageStatus = "Parada: o Terminal está bloqueado ou em manutenção.";
            return;
        }

        if (IdleTime() < (_coverageAbsent ? CoverageIdleAbsent : CoverageIdle))
        {
            _coverageStatus = _coverageAbsent
                ? "Esperando: alguém mexeu neste computador agora há pouco (responde 10 segundos depois de pararem)."
                : "Esperando: alguém está usando este computador (só responde depois de 90 segundos sem mexer no teclado ou no mouse).";
            return;
        }

        var tab = _tabs.FirstOrDefault(t => !t.IsClosed && t.BlockedHost is null && IsWhatsApp(t));
        if (tab?.View.CoreWebView2 is not { } core)
        {
            _coverageStatus = "Parada: o WhatsApp não está aberto dentro do Terminal. Abra o WhatsApp pelo Terminal e deixe aberto.";
            return;
        }

        _coverageBusy = true;
        try
        {
            // Primeiro o que a supervisão aprovou; uma ação por volta, para não parecer robô.
            if (_coverage == CoverageOn && (await api.GetCoverageOutboxAsync(_cts.Token)).FirstOrDefault() is { } approved)
            {
                var (sent, error) = await DeliverAsync(core, approved.Contact, approved.Message, search: true);
                await api.ReportCoverageAsync(approved.Id, sent, holding: false, error, _cts.Token);
                return;
            }

            var now = DateTime.UtcNow;
            var unread = await UnreadChatsAsync(core);
            if (unread is null)
            {
                _coverageStatus = "Parada: o WhatsApp está aberto, mas a lista de conversas não apareceu (pode estar pedindo para ler o QR code ou ainda carregando).";
                return;
            }

            var contact = unread.FirstOrDefault()
                ?? _coverageRetry.Where(r => r.Value <= now).Select(r => r.Key).FirstOrDefault();
            if (contact is null)
            {
                _coverageStatus = $"Vigiando: nenhuma conversa não lida no WhatsApp ({DateTime.Now:HH:mm}).";
                return;
            }

            if (!await OpenChatAsync(core, contact, search: _coverageRetry.ContainsKey(contact)))
            {
                _coverageRetry.Remove(contact);
                return;
            }

            var chat = await ReadOpenChatAsync(core);
            var transcript = chat.Text;
            if (!chat.Is(contact) || !transcript.StartsWith('['))
            {
                // Abriu outra conversa, ou a página não separou as mensagens: melhor não responder nada.
                var why = !chat.Open ? $"a conversa não abriu ao clicar (na tela: {(chat.Hint.Length > 0 ? chat.Hint : "sem detalhes")})"
                    : !chat.Is(contact) ? $"o cabeçalho da conversa aberta mostra \"{string.Join(" | ", chat.Names.Prepend(chat.Contact).Where(n => n.Length > 0).Distinct().Take(4))}\""
                    : "abri a conversa, mas não achei as mensagens nela";
                Log.Info($"cobertura: conversa não reconhecida ({why})");
                _coverageStatus = $"Achei mensagem não lida de {contact}, mas não consegui ler: {why} ({DateTime.Now:HH:mm}).";
                _coverageRetry.Remove(contact);
                return;
            }

            var action = await api.AskCoverageAsync(contact, transcript, _cts.Token);
            _coverageStatus = $"Última conversa tratada: {contact} ({DateTime.Now:HH:mm}).";
            if (action.Action == CoverageAction.Retry)
            {
                if (_coverageRetry.Count < 50)
                {
                    _coverageRetry[contact] = now + CoverageRetry;
                }

                return;
            }

            _coverageRetry.Remove(contact);
            if (action.Action == CoverageAction.Send && action.Id is { } id && action.Message is { Length: > 0 } message && _coverage == CoverageOn)
            {
                var (sent, error) = await SendInOpenChatAsync(core, message);
                await api.ReportCoverageAsync(id, sent, action.Holding, error, _cts.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("cobertura de ausência", ex);
            _coverageStatus = $"Erro ao tratar o WhatsApp: {ex.Message}";
        }
        finally
        {
            _coverageBusy = false;
        }
    }

    /// <summary>Abre a conversa do contato, confere que é ela e envia.</summary>
    private async Task<(bool Sent, string? Error)> DeliverAsync(CoreWebView2 core, string contact, string message, bool search)
    {
        if (!await OpenChatAsync(core, contact, search))
        {
            return (false, "Não achei a conversa desse contato no WhatsApp.");
        }

        return (await ReadOpenChatAsync(core)).Is(contact) ? await SendInOpenChatAsync(core, message) : (false, "A conversa aberta não era a desse contato; nada foi enviado.");
    }

    private async Task<(bool Sent, string? Error)> SendInOpenChatAsync(CoreWebView2 core, string message)
    {
        var typed = await RunAsync(core, $"({TypeScript})({JsonSerializer.Serialize(message)})");
        if (typed != "\"ok\"")
        {
            await RunAsync(core, ClearScript);
            return (false, "Não consegui escrever no campo de mensagem do WhatsApp.");
        }

        await Task.Delay(700, _cts.Token);
        if (await RunAsync(core, SendScript) != "true")
        {
            await RunAsync(core, ClearScript);
            return (false, "Não achei o botão de enviar do WhatsApp.");
        }

        // Confere na própria conversa que a mensagem apareceu como enviada.
        await Task.Delay(1800, _cts.Token);
        var transcript = (await ReadOpenChatAsync(core)).Text;
        var start = Normalize(message);
        start = start.Length > 30 ? start[..30] : start;
        var mine = transcript.Split('\n').Where(l => l.Contains("] Atendente:", StringComparison.Ordinal)).TakeLast(3);
        return mine.Any(l => Normalize(l).Contains(start, StringComparison.Ordinal))
            ? (true, null)
            : (false, "Não confirmei a mensagem na conversa. Confira no WhatsApp antes de aprovar de novo.");
    }

    private async Task<bool> OpenChatAsync(CoreWebView2 core, string contact, bool search)
    {
        if (await ClickChatAsync(core, contact))
        {
            return true;
        }

        var name = JsonSerializer.Serialize(contact);
        if (!search || await RunAsync(core, $"({SearchScript})({name})") != "true")
        {
            return false;
        }

        await Task.Delay(1800, _cts.Token);
        var opened = await ClickChatAsync(core, contact);
        await RunAsync(core, $"({SearchScript})(\"\")");
        return opened;
    }

    /// <summary>
    /// Clica na conversa da lista como um mouse de verdade: acha onde ela está na tela e pede ao próprio navegador
    /// (protocolo DevTools) que faça o clique ali. O WhatsApp ignora eventos criados por script; este ele aceita.
    /// Se o navegador não aceitar o pedido, cai no clique por script.
    /// </summary>
    private async Task<bool> ClickChatAsync(CoreWebView2 core, string contact)
    {
        var name = JsonSerializer.Serialize(contact);
        var where = await RunAsync(core, $"({LocateScript})({name})");
        if (where is null or "null" or "false")
        {
            return false;
        }

        double x, y;
        try
        {
            using var doc = JsonDocument.Parse(where);
            x = Math.Round(doc.RootElement.GetProperty("x").GetDouble(), 1);
            y = Math.Round(doc.RootElement.GetProperty("y").GetDouble(), 1);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }

        try
        {
            var at = $"\"x\":{x.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseMoved\",{at}}}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mousePressed\",{at},\"button\":\"left\",\"buttons\":1,\"clickCount\":1}}");
            await Task.Delay(80, _cts.Token);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseReleased\",{at},\"button\":\"left\",\"buttons\":0,\"clickCount\":1}}");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Log.Error("cobertura: clique pelo navegador", ex);
            if (await RunAsync(core, $"({OpenScript})({name})") != "true")
            {
                return false;
            }
        }

        await Task.Delay(2500, _cts.Token);
        return true;
    }

    /// <summary>Nomes das conversas não lidas; null quando a lista de conversas do WhatsApp não está na tela.</summary>
    private async Task<List<string>?> UnreadChatsAsync(CoreWebView2 core)
    {
        var json = await RunAsync(core, UnreadScript);
        try
        {
            if (json is not { Length: > 0 })
            {
                return [];
            }

            return json == "null" ? null : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A conversa aberta no WhatsApp: o nome do cabeçalho (e os outros textos dele) e as mensagens.</summary>
    private sealed record OpenChat(string Contact, string Text, string[] Names, bool Open, string Hint = "")
    {
        public bool Is(string expected) => SameContact(Contact, expected) || Names.Any(n => n.Length >= 3 && SameContact(n, expected));
    }

    private async Task<OpenChat> ReadOpenChatAsync(CoreWebView2 core)
    {
        if (await RunAsync(core, ScreenScript) is not { Length: > 0 } json)
        {
            return new("", "", [], false);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new("", "", [], false);
        }

        string Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        string[] names = root.TryGetProperty("names", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? "")]
            : [];
        var text = Field("text");
        return new(Field("contact"), text, names, text != "Nenhuma conversa aberta no WhatsApp.", Field("hint"));
    }

    private static async Task<string?> RunAsync(CoreWebView2 core, string script) => await WithinAsync(core.ExecuteScriptAsync(script));

    private static bool SameContact(string opened, string expected)
    {
        var a = Normalize(opened);
        var b = Normalize(expected);
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Replace("*", "").Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // ------------------------------------------------------------- scripts do WhatsApp Web

    /// <summary>Nomes das conversas com mensagem não lida na lista lateral (até 10).</summary>
    private const string UnreadScript =
        """
        (() => { try {
          const lado = document.querySelector('#pane-side');
          if (!lado) return null;
          const nomes = [];
          for (const linha of lado.querySelectorAll('[role="listitem"], [role="row"]')) {
            if (!linha.querySelector('span[aria-label*="não lida" i], span[aria-label*="unread" i]')) continue;
            const t = linha.querySelector('span[title]');
            const nome = t ? (t.getAttribute('title') || '').trim() : '';
            if (nome && !nomes.includes(nome)) nomes.push(nome);
            if (nomes.length >= 10) break;
          }
          return nomes;
        } catch (e) { return []; } })()
        """;

    /// <summary>Onde está, na tela, a conversa de nome exato da lista lateral: {x, y} do centro dela (ou null).</summary>
    private const string LocateScript =
        """
        (nome) => { try {
          const lado = document.querySelector('#pane-side');
          if (!lado) return null;
          const alvo = [...lado.querySelectorAll('span[title]')].find(s => (s.getAttribute('title') || '').trim() === nome);
          if (!alvo) return null;
          const linha = alvo.closest('[role="listitem"], [role="row"]') || alvo;
          linha.scrollIntoView({ block: 'center' });
          const r = linha.getBoundingClientRect();
          if (!r.width || !r.height) return null;
          return { x: r.left + Math.min(r.width / 2, 160), y: r.top + r.height / 2 };
        } catch (e) { return null; } }
        """;

    /// <summary>Abre a conversa de nome exato que está na lista lateral (clique por script; só se o navegador recusar o clique real).</summary>
    private const string OpenScript =
        """
        (nome) => { try {
          const lado = document.querySelector('#pane-side');
          if (!lado) return false;
          const alvo = [...lado.querySelectorAll('span[title]')].find(s => (s.getAttribute('title') || '').trim() === nome);
          if (!alvo) return false;
          const linha = alvo.closest('[role="listitem"], [role="row"]') || alvo;
          const clicavel = linha.querySelector('[tabindex]') || linha.querySelector('[role="gridcell"], [role="button"]') || linha;
          const r = clicavel.getBoundingClientRect();
          const pos = { bubbles: true, cancelable: true, view: window, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2, button: 0, buttons: 1 };
          // O WhatsApp é uma página React: precisa dos eventos de ponteiro, não só do click.
          for (const tipo of ['pointerdown', 'mousedown']) clicavel.dispatchEvent(tipo === 'pointerdown' ? new PointerEvent(tipo, { ...pos, pointerId: 1, pointerType: 'mouse', isPrimary: true }) : new MouseEvent(tipo, pos));
          for (const tipo of ['pointerup', 'mouseup', 'click']) clicavel.dispatchEvent(tipo === 'pointerup' ? new PointerEvent(tipo, { ...pos, pointerId: 1, pointerType: 'mouse', isPrimary: true, buttons: 0 }) : new MouseEvent(tipo, { ...pos, buttons: 0 }));
          try { clicavel.focus(); clicavel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true })); } catch (e) { }
          return true;
        } catch (e) { return false; } }
        """;

    /// <summary>Escreve na busca de conversas (texto vazio limpa a busca).</summary>
    private const string SearchScript =
        """
        (texto) => { try {
          const busca = document.querySelector('#side div[contenteditable="true"]');
          if (!busca) return false;
          busca.focus();
          document.execCommand('selectAll', false, null);
          document.execCommand('delete', false, null);
          if (texto) document.execCommand('insertText', false, texto);
          return true;
        } catch (e) { return false; } }
        """;

    /// <summary>Escreve a mensagem no campo da conversa aberta (colando, para manter as quebras de linha).</summary>
    private const string TypeScript =
        """
        (texto) => { try {
          const caixa = document.querySelector('#main footer div[contenteditable="true"][role="textbox"]') || document.querySelector('#main footer div[contenteditable="true"]');
          if (!caixa) return 'sem-caixa';
          caixa.focus();
          if ((caixa.innerText || '').trim()) { document.execCommand('selectAll', false, null); document.execCommand('delete', false, null); }
          const dados = new DataTransfer();
          dados.setData('text/plain', texto);
          caixa.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dados, bubbles: true, cancelable: true }));
          if (!(caixa.innerText || '').trim()) document.execCommand('insertText', false, texto);
          return (caixa.innerText || '').trim() ? 'ok' : 'vazio';
        } catch (e) { return 'erro'; } }
        """;

    private const string SendScript =
        """
        (() => { try {
          const icone = document.querySelector('#main footer [data-icon="send"], #main footer [data-icon="wds-ic-send-filled"]');
          const botao = (icone && icone.closest('button')) || document.querySelector('#main footer button[aria-label="Enviar"], #main footer button[aria-label="Send"]');
          if (!botao) return false;
          botao.click();
          return true;
        } catch (e) { return false; } })()
        """;

    /// <summary>Apaga o que ficou no campo de mensagem quando o envio não deu certo.</summary>
    private const string ClearScript =
        """
        (() => { try {
          const caixa = document.querySelector('#main footer div[contenteditable="true"]');
          if (caixa && (caixa.innerText || '').trim()) { caixa.focus(); document.execCommand('selectAll', false, null); document.execCommand('delete', false, null); }
          return true;
        } catch (e) { return false; } })()
        """;

    // ------------------------------------------------------------- tempo sem uso do computador

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }
}
