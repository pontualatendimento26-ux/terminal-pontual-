using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Automation;
using Microsoft.Win32;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Lê, pela Automação da Interface do Windows, o endereço da aba visível de cada janela do Google Chrome
/// (todas as janelas, de todos os perfis). Só lê a barra de endereço: não clica, não digita e não lê a página.
/// Roda fora da linha da interface; uma janela que some no meio da leitura é ignorada.
/// </summary>
internal static class ChromeReader
{
    private static readonly Condition WindowCondition = new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1");
    private static readonly Condition EditCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    /// <summary>
    /// Lê os endereços e, quando o domínio da aba visível está em <paramref name="isBlocked"/>, troca a aba por uma página de
    /// "site bloqueado". É a garantia do bloqueio mesmo sem a política do Chrome (sem permissão no registro, ou página
    /// que já estava aberta antes do bloqueio). Devolve os endereços lidos e os domínios desviados.
    /// </summary>
    public static (IReadOnlyList<string> Addresses, IReadOnlyList<string> Redirected) Scan(Func<string, bool> isBlocked)
    {
        var redirected = new List<string>();
        var addresses = ReadAddresses((window, edit, value, text) =>
        {
            if (ChromeWatch.HostFromAddress(text) is { } host && isBlocked(host))
            {
                if (TryRedirect(window, edit, value, host))
                {
                    redirected.Add(host);
                }

                return true;
            }

            return false;
        });
        return (addresses, redirected);
    }

    public static IReadOnlyList<string> ReadAddresses() => ReadAddresses(null);

    private static IReadOnlyList<string> ReadAddresses(Func<AutomationElement, AutomationElement, ValuePattern, string, bool>? handled)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("chrome"))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var addresses = new List<string>();
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, WindowCondition))
        {
            try
            {
                if (!ids.Contains(window.Current.ProcessId))
                {
                    continue;
                }

                // A barra de endereço é o primeiro campo de texto da janela (fica na barra de ferramentas, antes
                // da página). Se o primeiro campo não for ela, a janela é ignorada: nunca se lê um campo da página.
                // Com o foco na barra, a atendente ainda está digitando: o texto ainda não é um endereço aberto.
                var edit = window.FindFirst(TreeScope.Descendants, EditCondition);
                if (edit is null || !IsAddressBar(edit.Current.Name) || edit.Current.HasKeyboardFocus)
                {
                    continue;
                }

                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern value
                    && value.Current.Value is { Length: > 0 and <= 2048 } text
                    && handled?.Invoke(window, edit, value, text) != true)
                {
                    addresses.Add(text);
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
    /// Leva a aba para a página de bloqueio: foco na barra de endereço, troca o texto e Enter. O Enter só é enviado se a
    /// janela do Chrome estiver na frente e o foco na barra dela, para nunca cair em outro lugar (uma conversa, por exemplo).
    /// </summary>
    private static bool TryRedirect(AutomationElement window, AutomationElement edit, ValuePattern value, string host)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero || ChromeNative.IsIconic(handle))
        {
            return false;
        }

        edit.SetFocus();
        Thread.Sleep(250);
        if (ChromeNative.GetForegroundWindow() != handle || !edit.Current.HasKeyboardFocus)
        {
            return false;
        }

        value.SetValue(BlockPage(host));
        Thread.Sleep(150);
        if (ChromeNative.GetForegroundWindow() != handle || !edit.Current.HasKeyboardFocus)
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

    // "Barra de endereço e de pesquisa" (Chrome em português), "Address and search bar" (inglês), "Barra de direcciones" (espanhol).
    private static bool IsAddressBar(string? name) =>
        name is not null
        && (name.Contains("endereço", StringComparison.OrdinalIgnoreCase)
            || name.Contains("address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("direcci", StringComparison.OrdinalIgnoreCase));
}

internal static class ChromeNative
{
    public const byte VkReturn = 0x0D;
    public const uint KeyUp = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

/// <summary>
/// Grava os domínios que o TI bloqueou na política do Google Chrome desta conta
/// (HKEY_CURRENT_USER\Software\Policies\Google\Chrome\URLBlocklist). O Chrome aplica a mudança sozinho, sem reiniciar.
/// Chaves de política só aceitam escrita de administrador; o instalador com bloqueio dá à conta da atendente
/// permissão de escrita só nesta chave. Sem essa permissão (instalação de teste), o bloqueio não é aplicado e o
/// Terminal continua só avisando.
/// </summary>
internal static class ChromePolicy
{
    public const string BlocklistKey = @"Software\Policies\Google\Chrome\URLBlocklist";
    private static bool _warned;

    /// <summary>Devolve se a lista no registro ficou igual a <paramref name="hosts"/>.</summary>
    public static bool ApplyBlocklist(IReadOnlyList<string> hosts)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(BlocklistKey, writable: true)
                ?? (hosts.Count == 0 ? null : Registry.CurrentUser.CreateSubKey(BlocklistKey, writable: true));
            if (key is null)
            {
                return true;
            }

            var current = key.GetValueNames()
                .Select(n => (Number: int.TryParse(n, out var i) ? i : int.MaxValue, Value: key.GetValue(n) as string))
                .OrderBy(v => v.Number)
                .Select(v => v.Value)
                .ToArray();
            if (current.SequenceEqual(hosts))
            {
                return true;
            }

            foreach (var name in key.GetValueNames())
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            for (var i = 0; i < hosts.Count; i++)
            {
                key.SetValue((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), hosts[i], RegistryValueKind.String);
            }

            Log.Info($"Chrome: {hosts.Count} domínio(s) bloqueado(s) pelo TI gravados na política.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            if (!_warned)
            {
                _warned = true;
                Log.Info($"Chrome: sem permissão para gravar a lista de bloqueio ({ex.Message}). O Terminal continua só avisando o TI.");
            }

            return false;
        }
    }
}
