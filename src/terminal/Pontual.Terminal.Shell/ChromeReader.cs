using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Automation;
using Microsoft.Win32;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Um navegador vigiado: o nome que o TI vê, o processo, a classe da janela principal e o tipo de controle da barra de
/// endereço (campo de texto no Chrome, Edge e Opera; caixa de combinação no Firefox).
/// </summary>
internal sealed record Browser(string Name, string Process, string WindowClass, ControlType AddressBar, bool ReportsFocus = true)
{
    public Condition AddressCondition { get; } = new PropertyCondition(AutomationElement.ControlTypeProperty, AddressBar);
}

/// <summary>Um endereço lido na barra de um navegador.</summary>
internal sealed record BrowserAddress(string Browser, string Address);

/// <summary>Um domínio bloqueado cuja aba foi desviada para a página de bloqueio.</summary>
internal sealed record BrowserRedirect(string Browser, string Host);

/// <summary>
/// Lê, pela Automação da Interface do Windows, o endereço da aba visível de cada janela dos navegadores
/// (Google Chrome, Microsoft Edge, Opera e Mozilla Firefox; todas as janelas, de todos os perfis).
/// Só lê a barra de endereço: não clica, não digita e não lê a página.
/// Roda fora da linha da interface; uma janela que some no meio da leitura é ignorada.
/// </summary>
internal static class ChromeReader
{
    /// <summary>
    /// Chrome, Edge e Opera são feitos sobre o Chromium e têm a mesma classe de janela; o Firefox tem a dele.
    /// Conferido em 20/11/2026: Edge "Barra de pesquisa e endereços" (Edit), Opera GX "Barra de endereços" (Edit),
    /// Firefox "Pesquise com Google ou digite um endereço" (ComboBox, valor sem "https://"). O Firefox não informa o foco
    /// pela automação (o campo diz "sem foco" mesmo selecionado): o desvio dele usa Ctrl+L.
    /// </summary>
    public static readonly IReadOnlyList<Browser> Browsers =
    [
        new("Google Chrome", "chrome", "Chrome_WidgetWin_1", ControlType.Edit),
        new("Microsoft Edge", "msedge", "Chrome_WidgetWin_1", ControlType.Edit),
        new("Opera", "opera", "Chrome_WidgetWin_1", ControlType.Edit),
        new("Mozilla Firefox", "firefox", "MozillaWindowClass", ControlType.ComboBox, ReportsFocus: false),
    ];

    private static readonly Condition WindowCondition = new OrCondition(
        [.. Browsers.Select(b => b.WindowClass).Distinct(StringComparer.Ordinal)
            .Select(c => (Condition)new PropertyCondition(AutomationElement.ClassNameProperty, c))]);

    /// <summary>
    /// Lê os endereços e, quando o domínio da aba visível está em <paramref name="isBlocked"/>, troca a aba por uma página de
    /// "site bloqueado". É a garantia do bloqueio mesmo sem a política do navegador (sem permissão no registro, Opera, que
    /// não aceita políticas, ou página que já estava aberta antes do bloqueio). Devolve os endereços lidos e os desvios.
    /// </summary>
    public static (IReadOnlyList<BrowserAddress> Addresses, IReadOnlyList<BrowserRedirect> Redirected) Scan(Func<string, bool> isBlocked)
    {
        var redirected = new List<BrowserRedirect>();
        var addresses = ReadAddresses((browser, window, edit, value, text) =>
        {
            if (ChromeWatch.HostFromAddress(text) is { } host && isBlocked(host))
            {
                if (TryRedirect(browser, window, edit, value, host))
                {
                    redirected.Add(new BrowserRedirect(browser.Name, host));
                }

                return true;
            }

            return false;
        });
        return (addresses, redirected);
    }

    private static IReadOnlyList<BrowserAddress> ReadAddresses(Func<Browser, AutomationElement, AutomationElement, ValuePattern, string, bool> handled)
    {
        // Processo de cada navegador aberto. O WebView2 do próprio Terminal é "msedgewebview2", outro nome: não entra.
        var owners = new Dictionary<int, Browser>();
        foreach (var browser in Browsers)
        {
            foreach (var process in Process.GetProcessesByName(browser.Process))
            {
                using (process)
                {
                    owners[process.Id] = browser;
                }
            }
        }

        if (owners.Count == 0)
        {
            return [];
        }

        var addresses = new List<BrowserAddress>();
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, WindowCondition))
        {
            try
            {
                if (!owners.TryGetValue(window.Current.ProcessId, out var browser)
                    || !string.Equals(window.Current.ClassName, browser.WindowClass, StringComparison.Ordinal))
                {
                    continue;
                }

                // A barra de endereço é o primeiro campo do tipo dela na janela (fica na barra de ferramentas, antes
                // da página). Se o primeiro campo não for ela, a janela é ignorada: nunca se lê um campo da página.
                // Com o foco na barra, a atendente ainda está digitando: o texto ainda não é um endereço aberto.
                var edit = window.FindFirst(TreeScope.Descendants, browser.AddressCondition);
                if (edit is null || !IsAddressBar(edit.Current.Name) || (browser.ReportsFocus && edit.Current.HasKeyboardFocus))
                {
                    continue;
                }

                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern value)
                {
                    continue;
                }

                // Firefox não informa o foco: o texto da barra pode ser o que a atendente ainda está digitando.
                // Nele vale o endereço da página da aba visível, que só muda quando a página abre.
                var text = browser.ReportsFocus ? value.Current.Value : VisiblePageAddress(window);
                if (text is { Length: > 0 and <= 2048 } && !handled(browser, window, edit, value, text))
                {
                    addresses.Add(new BrowserAddress(browser.Name, text));
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // A janela fechou ou trocou durante a leitura.
            }
        }

        return addresses;
    }

    /// <summary>
    /// Endereço da página da aba visível (Firefox: cada aba é um "documento"; as escondidas ficam fora da tela).
    /// Percorre só a moldura do navegador: não entra nas páginas (o WhatsApp Web tem milhares de elementos).
    /// </summary>
    private static string? VisiblePageAddress(AutomationElement window)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Queue<AutomationElement>();
        pending.Enqueue(window);
        var visited = 0;
        while (pending.Count > 0 && visited < 600)
        {
            var child = walker.GetFirstChild(pending.Dequeue());
            while (child is not null && visited++ < 600)
            {
                if (child.Current.ControlType == ControlType.Document)
                {
                    if (!child.Current.IsOffscreen
                        && child.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                        && pattern is ValuePattern page
                        && page.Current.Value is { } url
                        && url.Contains("://", StringComparison.Ordinal))
                    {
                        return url;
                    }
                }
                else
                {
                    pending.Enqueue(child);
                }

                child = walker.GetNextSibling(child);
            }
        }

        return null;
    }

    /// <summary>
    /// Leva a aba para a página de bloqueio: foco na barra de endereço, troca o texto e Enter. O Enter só é enviado se a
    /// janela do navegador estiver na frente e o foco na barra dela, para nunca cair em outro lugar (uma conversa, por exemplo).
    /// </summary>
    private static bool TryRedirect(Browser browser, AutomationElement window, AutomationElement edit, ValuePattern value, string host)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero || ChromeNative.IsIconic(handle))
        {
            return false;
        }

        edit.SetFocus();
        Thread.Sleep(250);
        if (ChromeNative.GetForegroundWindow() != handle)
        {
            return false;
        }

        if (!edit.Current.HasKeyboardFocus)
        {
            if (browser.ReportsFocus)
            {
                return false;
            }

            // Firefox: não dá para conferir o foco pela automação. Com a janela dele na frente, Ctrl+L leva o foco
            // para a barra de endereço (atalho do próprio navegador), e só então o texto é trocado.
            ChromeNative.PressCtrlL();
            Thread.Sleep(200);
            if (ChromeNative.GetForegroundWindow() != handle)
            {
                return false;
            }
        }

        value.SetValue(BlockPage(host));
        Thread.Sleep(150);
        if (ChromeNative.GetForegroundWindow() != handle || (browser.ReportsFocus && !edit.Current.HasKeyboardFocus))
        {
            return false;
        }

        ChromeNative.keybd_event(ChromeNative.VkReturn, 0, 0, UIntPtr.Zero);
        ChromeNative.keybd_event(ChromeNative.VkReturn, 0, ChromeNative.KeyUp, UIntPtr.Zero);
        return true;
    }

    // Página sem rede: o endereço "data:" não é site, então a vigia não avisa nada sobre ela.
    private static string BlockPage(string host)
    {
        var safe = System.Net.WebUtility.HtmlEncode(host);
        var html = "<!doctype html><meta charset=utf-8><title>Site bloqueado</title>"
            + "<body style=\"font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:90vh;color:#1f2937;background:#f8fafc\">"
            + $"<div style=\"text-align:center\"><h1 style=\"font-size:28px\">Este site foi bloqueado pelo TI</h1>"
            + $"<p style=\"font-size:18px\">{safe} não faz parte do trabalho. Se você precisa dele, fale com o TI.</p></div>";
        return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
    }

    // Nomes da barra: "Barra de endereço e de pesquisa" (Chrome), "Barra de endereços e pesquisa" (Edge),
    // "... digite um endereço" (Firefox), "Address and search bar" (inglês), "Barra de direcciones" (espanhol).
    private static bool IsAddressBar(string? name) =>
        name is not null
        && (name.Contains("endereço", StringComparison.OrdinalIgnoreCase)
            || name.Contains("address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("direcci", StringComparison.OrdinalIgnoreCase));
}

internal static class ChromeNative
{
    public const byte VkReturn = 0x0D;
    public const byte VkControl = 0x11;
    public const byte VkL = 0x4C;
    public const uint KeyUp = 0x0002;

    /// <summary>Ctrl+L: em todos os navegadores, seleciona a barra de endereço da janela que está na frente.</summary>
    public static void PressCtrlL()
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

/// <summary>
/// Grava os domínios que o TI bloqueou na política de cada navegador desta conta (HKEY_CURRENT_USER\Software\Policies\...).
/// Chrome e Edge aplicam a mudança sozinhos, sem reiniciar; o Firefox só lê a política quando é aberto (até lá vale o
/// desvio da aba); o Opera não aceita políticas (só o desvio). Chaves de política só aceitam escrita de administrador;
/// o instalador com bloqueio dá à conta da atendente permissão de escrita só nestas chaves. Sem essa permissão
/// (instalação de teste ou instalada antes desta versão), a política não é gravada e o desvio da aba continua valendo.
/// </summary>
internal static class BrowserPolicy
{
    public const string ChromeKey = @"Software\Policies\Google\Chrome\URLBlocklist";
    public const string EdgeKey = @"Software\Policies\Microsoft\Edge\URLBlocklist";
    public const string FirefoxKey = @"Software\Policies\Mozilla\Firefox\WebsiteFilter\Block";

    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    /// <summary>Formato do Firefox: padrão de endereço. "*.exemplo.com" vale para o domínio e os subdomínios.</summary>
    public static IReadOnlyList<string> FirefoxPatterns(IEnumerable<string> hosts) =>
        [.. hosts.Select(h => System.Net.IPAddress.TryParse(h, out _) ? $"*://{h}/*" : $"*://*.{h}/*")];

    /// <summary>
    /// Devolve se a lista no registro ficou igual a <paramref name="values"/>. Sem <paramref name="create"/>, chave que
    /// não existe fica como está (a do Edge só existe nas instalações com bloqueio).
    /// </summary>
    public static bool Apply(string name, string keyPath, IReadOnlyList<string> values, bool create)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
                ?? (values.Count == 0 || !create ? null : Registry.CurrentUser.CreateSubKey(keyPath, writable: true));
            if (key is null)
            {
                return true;
            }

            var current = key.GetValueNames()
                .Select(n => (Number: int.TryParse(n, out var i) ? i : int.MaxValue, Value: key.GetValue(n) as string))
                .OrderBy(v => v.Number)
                .Select(v => v.Value)
                .ToArray();
            if (current.SequenceEqual(values))
            {
                return true;
            }

            foreach (var valueName in key.GetValueNames())
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            for (var i = 0; i < values.Count; i++)
            {
                key.SetValue((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), values[i], RegistryValueKind.String);
            }

            Log.Info($"{name}: {values.Count} item(ns) gravados na lista de bloqueio.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            lock (Warned)
            {
                if (Warned.Add(name))
                {
                    Log.Info($"{name}: sem permissão para gravar a lista de bloqueio ({ex.Message}). Vale só o desvio da aba.");
                }
            }

            return false;
        }
    }
}
