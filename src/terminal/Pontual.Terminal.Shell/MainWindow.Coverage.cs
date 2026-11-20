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
    /// <summary>Sem teclado nem mouse por este tempo, o computador está livre para a cobertura usar o WhatsApp.</summary>
    private static readonly TimeSpan CoverageIdle = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CoverageRetry = TimeSpan.FromMinutes(1);

    private string _coverage = "";
    private System.Windows.Threading.DispatcherTimer? _coverageTimer;
    private bool _coverageBusy;
    /// <summary>Conversas já abertas (e por isso marcadas como lidas) que ainda esperam uma nova tentativa.</summary>
    private readonly Dictionary<string, DateTime> _coverageRetry = [];

    private void UpdateCoverage(string mode)
    {
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
            return;
        }

        if (_coverageTimer is null)
        {
            _coverageTimer = new System.Windows.Threading.DispatcherTimer { Interval = CoverageEvery };
            _coverageTimer.Tick += async (_, _) => await CoverAsync();
        }

        _coverageTimer.Start();
    }

    private async Task CoverAsync()
    {
        if (_coverageBusy || _coverage.Length == 0 || _api is not { } api || _state != HeartbeatResponse.StateActive || IdleTime() < CoverageIdle)
        {
            return;
        }

        var tab = _tabs.FirstOrDefault(t => !t.IsClosed && t.BlockedHost is null && IsWhatsApp(t));
        if (tab?.View.CoreWebView2 is not { } core)
        {
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
            var contact = (await UnreadChatsAsync(core)).FirstOrDefault()
                ?? _coverageRetry.Where(r => r.Value <= now).Select(r => r.Key).FirstOrDefault();
            if (contact is null)
            {
                return;
            }

            if (!await OpenChatAsync(core, contact, search: _coverageRetry.ContainsKey(contact)))
            {
                _coverageRetry.Remove(contact);
                return;
            }

            var (opened, transcript) = await ReadOpenChatAsync(core);
            if (!SameContact(opened, contact) || !transcript.StartsWith('['))
            {
                // Abriu outra conversa, ou a página não separou as mensagens: melhor não responder nada.
                Log.Info($"cobertura: conversa não reconhecida ({(SameContact(opened, contact) ? "mensagens sem formato" : "contato diferente")})");
                _coverageRetry.Remove(contact);
                return;
            }

            var action = await api.AskCoverageAsync(contact, transcript, _cts.Token);
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

        var (opened, _) = await ReadOpenChatAsync(core);
        return SameContact(opened, contact) ? await SendInOpenChatAsync(core, message) : (false, "A conversa aberta não era a desse contato; nada foi enviado.");
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
        var (_, transcript) = await ReadOpenChatAsync(core);
        var start = Normalize(message);
        start = start.Length > 30 ? start[..30] : start;
        var mine = transcript.Split('\n').Where(l => l.Contains("] Atendente:", StringComparison.Ordinal)).TakeLast(3);
        return mine.Any(l => Normalize(l).Contains(start, StringComparison.Ordinal))
            ? (true, null)
            : (false, "Não confirmei a mensagem na conversa. Confira no WhatsApp antes de aprovar de novo.");
    }

    private async Task<bool> OpenChatAsync(CoreWebView2 core, string contact, bool search)
    {
        var name = JsonSerializer.Serialize(contact);
        if (await RunAsync(core, $"({OpenScript})({name})") == "true")
        {
            await Task.Delay(1500, _cts.Token);
            return true;
        }

        if (!search || await RunAsync(core, $"({SearchScript})({name})") != "true")
        {
            return false;
        }

        await Task.Delay(1800, _cts.Token);
        var opened = await RunAsync(core, $"({OpenScript})({name})") == "true";
        await RunAsync(core, $"({SearchScript})(\"\")");
        if (opened)
        {
            await Task.Delay(1500, _cts.Token);
        }

        return opened;
    }

    private async Task<List<string>> UnreadChatsAsync(CoreWebView2 core)
    {
        var json = await RunAsync(core, UnreadScript);
        try
        {
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<(string Contact, string Text)> ReadOpenChatAsync(CoreWebView2 core)
    {
        if (await RunAsync(core, ScreenScript) is not { Length: > 0 } json)
        {
            return ("", "");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ("", "");
        }

        string Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return (Field("contact"), Field("text"));
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
          if (!lado) return [];
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

    /// <summary>Abre a conversa de nome exato que está na lista lateral.</summary>
    private const string OpenScript =
        """
        (nome) => { try {
          const lado = document.querySelector('#pane-side');
          if (!lado) return false;
          const alvo = [...lado.querySelectorAll('span[title]')].find(s => (s.getAttribute('title') || '').trim() === nome);
          if (!alvo) return false;
          const linha = alvo.closest('[role="listitem"], [role="row"]') || alvo;
          const clicavel = linha.querySelector('[tabindex]') || linha;
          for (const tipo of ['mousedown', 'mouseup', 'click']) clicavel.dispatchEvent(new MouseEvent(tipo, { bubbles: true, cancelable: true, view: window }));
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
