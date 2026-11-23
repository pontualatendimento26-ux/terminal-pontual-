using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Aplicativos do Windows que, no Windows 11, são aplicativos da Loja: o <c>calc.exe</c> da pasta System32 é só um lançador,
/// que pede ao Windows para abrir a Calculadora e sai na hora. Na conta bloqueada da atendente esse pedido do lançador não
/// chega a abrir nada (conferido em 20/11/2026: "Abriu Calculadora" e "Fechou Calculadora" um segundo depois, sem janela).
/// Aqui o Terminal pede a abertura direto ao gerenciador de ativação do Windows, pelo identificador do aplicativo, e recebe
/// o processo aberto, que entra em "Abertos agora" como qualquer outro.
/// </summary>
/// <remarks>
/// As Notas Autoadesivas não têm nem o lançador: desde o Windows 10 1607 o <c>StikyNot.exe</c> não existe mais e só há o
/// pacote da Loja. Para elas (e para o que mais entrar em <see cref="WithoutLauncher"/>) o Terminal localiza o executável
/// do pacote registrado para a conta da atendente (registro do usuário + manifesto do pacote), e é esse caminho que entra
/// no catálogo. A decisão do TI continua por nome do arquivo (<c>microsoft.notes.exe</c>).
/// </remarks>
internal static class StoreApps
{
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Aplicativos da Loja sem lançador na System32: família do pacote, Id do aplicativo no manifesto e nome para a atendente.</summary>
    private static readonly (string Family, string AppId, string Name)[] WithoutLauncher =
    [
        ("Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe", "App", "Notas Autoadesivas"),
    ];

    /// <summary>Identificador (AUMID) do aplicativo da Loja que cada executável abre: lançadores da System32 e os achados pelo pacote.</summary>
    private static readonly ConcurrentDictionary<string, string> ByExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calc.exe"] = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        ["notepad.exe"] = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
        ["mspaint.exe"] = "Microsoft.Paint_8wekyb3d8bbwe!App",
        ["snippingtool.exe"] = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
        ["stikynot.exe"] = "Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App",
        ["microsoft.notes.exe"] = "Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App",
    };

    public static bool IsStoreLauncher(string exe) => ByExe.ContainsKey(exe);

    /// <summary>Identificador do aplicativo da Loja que o lançador <paramref name="exe"/> abre, ou <c>null</c>.</summary>
    public static string? AumidFor(string exe) => ByExe.TryGetValue(exe, out var aumid) ? aumid : null;

    /// <summary>Editor "Microsoft Corporation" no nome da família dos pacotes da Loja.</summary>
    public const string MicrosoftPublisherId = "8wekyb3d8bbwe";

    /// <summary>
    /// Pacotes da Loja que não entram na lista nem liberados: console, Linux, instalador de pacotes, acesso remoto,
    /// ferramentas de desenvolvedor. Comparação pelo começo do nome da família.
    /// </summary>
    private static readonly string[] ExcludedFamilies =
    [
        "Microsoft.WindowsTerminal", "Microsoft.PowerShell", "Microsoft.DesktopAppInstaller", "MicrosoftCorporationII.WindowsSubsystemForLinux",
        "Microsoft.WSL", "CanonicalGroupLimited.", "TheDebianProject.", "KaliLinux.", "PythonSoftwareFoundation.", "Microsoft.DevHome",
        "Microsoft.Windows.DevHome", "MicrosoftCorporationII.QuickAssist", "Microsoft.RemoteDesktop", "MicrosoftCorporationII.Windows365",
        "Microsoft.WinDbg", "Microsoft.PowerToys", "Microsoft.SecHealthUI", "Microsoft.Sysinternals", "Microsoft.WindowsStore.Beta",
        "Microsoft.CommandPalette",
    ];

    /// <summary>Editores da Microsoft nos nomes de família: Microsoft Corporation e Skype.</summary>
    private static readonly string[] MicrosoftPublishers = [MicrosoftPublisherId, "kzf8qxf38zg5c"];

    /// <summary>Um aplicativo da Loja instalado para a conta: executável no pacote, nome, identificador e se é da Microsoft.</summary>
    internal sealed record PackagedApp(string Path, string Name, string Aumid, bool Microsoft);

    /// <summary>
    /// Todos os aplicativos da Loja registrados para a conta atual (a própria Microsoft Store entre eles), na versão mais nova
    /// de cada pacote. Ficam de fora os que não aparecem no menu Iniciar (<c>AppListEntry="none"</c>), os pacotes do sistema
    /// (pasta do Windows), os de <see cref="ExcludedFamilies"/> e os que <see cref="WithoutLauncher"/> e os lançadores já cobrem.
    /// Só lê registro e disco.
    /// </summary>
    public static List<PackagedApp> InstalledPackagedApps()
    {
        var result = new List<PackagedApp>();
        string[] fullNames;
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
            fullNames = packages?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return result;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var covered = new HashSet<string>(ByExe.Values.Select(a => a[..a.IndexOf('!')]), StringComparer.OrdinalIgnoreCase);

        // Nome completo: "Nome_1.2.3.4_x64__editor" (ou "Nome_1.2.3.4_neutral_~_editor"). Família = "Nome_editor".
        var byFamily = fullNames
            .Select(n => (FullName: n, First: n.IndexOf('_'), Last: n.LastIndexOf('_')))
            .Where(p => p.First > 0 && p.Last > p.First)
            .Select(p => (p.FullName, Family: p.FullName[..p.First] + "_" + p.FullName[(p.Last + 1)..], Prefix: p.FullName[..(p.First + 1)]))
            .GroupBy(p => p.Family, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byFamily)
        {
            var family = group.Key;
            if (covered.Contains(family) || ExcludedFamilies.Any(x => family.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var microsoft = MicrosoftPublishers.Any(p => family.EndsWith("_" + p, StringComparison.OrdinalIgnoreCase));
            foreach (var (fullName, _, prefix) in group.OrderByDescending(p => VersionOf(p.FullName, p.Prefix)))
            {
                string? root;
                try
                {
                    using var package = Registry.CurrentUser.OpenSubKey(PackagesKey + "\\" + fullName);
                    root = package?.GetValue("PackageRootFolder") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(root) || root.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    || ReadApplications(root, fullName) is not { } apps)
                {
                    continue;
                }

                foreach (var (appId, path, name) in apps)
                {
                    result.Add(new PackagedApp(path, name, $"{family}!{appId}", microsoft));
                }

                break;
            }
        }

        return result;
    }

    /// <summary>Aplicativos visíveis do manifesto em <paramref name="root"/>; <c>null</c> se não der para ler ou se for pacote de base/recursos.</summary>
    private static List<(string AppId, string Path, string Name)>? ReadApplications(string root, string fullName)
    {
        var manifest = System.IO.Path.Combine(root, "AppxManifest.xml");
        XDocument document;
        try
        {
            if (!File.Exists(manifest))
            {
                return null;
            }

            document = XDocument.Load(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or XmlException)
        {
            return null;
        }

        var elements = document.Descendants().ToArray();
        bool Flag(string name) => elements.Any(e => e.Name.LocalName == name && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        if (Flag("Framework") || Flag("ResourcePackage"))
        {
            return null;
        }

        var identity = (string?)elements.FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name") ?? "";
        var packageName = (string?)elements.FirstOrDefault(e => e.Name.LocalName == "Properties")?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
        var list = new List<(string, string, string)>();
        var rootPrefix = root.TrimEnd('\\') + "\\";
        foreach (var application in elements.Where(e => e.Name.LocalName == "Application"))
        {
            var appId = ((string?)application.Attribute("Id"))?.Trim();
            var executable = ((string?)application.Attribute("Executable"))?.Trim();
            var visual = application.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(executable)
                || string.Equals((string?)visual?.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, executable));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                continue;
            }

            var name = ResolveName((string?)visual?.Attribute("DisplayName"), fullName, identity)
                ?? ResolveName(packageName, fullName, identity)
                ?? System.IO.Path.GetFileNameWithoutExtension(full);
            list.Add((appId, full, name));
        }

        return list;
    }

    /// <summary>Nome do manifesto; os "ms-resource:" são lidos do pacote pelo Windows. <c>null</c> se não der.</summary>
    private static string? ResolveName(string? raw, string fullName, string identity)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();
        if (!raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var key = raw["ms-resource:".Length..];
        var uri = key.StartsWith("//", StringComparison.Ordinal) ? "ms-resource:" + key
            : key.Contains('/') ? $"ms-resource://{identity}/{key.TrimStart('/')}"
            : $"ms-resource://{identity}/resources/{key}";
        foreach (var candidate in new[] { uri, $"ms-resource://{identity}/{key.TrimStart('/')}" })
        {
            var buffer = new System.Text.StringBuilder(512);
            if (SHLoadIndirectString("@{" + fullName + "?" + candidate + "}", buffer, buffer.Capacity, IntPtr.Zero) == 0
                && buffer.Length > 0 && !buffer.ToString().StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                return buffer.ToString().Trim();
            }
        }

        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, System.Text.StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>
    /// Os aplicativos de <see cref="WithoutLauncher"/> instalados para a conta atual: caminho do executável dentro do pacote
    /// e nome. Só lê registro e disco; o que não estiver registrado para a conta fica de fora (e não abriria mesmo).
    /// </summary>
    public static IEnumerable<(string Path, string Name)> InstalledWithoutLauncher()
    {
        foreach (var (family, appId, name) in WithoutLauncher)
        {
            var path = LocateExecutable(family, appId);
            if (path is null && RegisterTried.TryAdd(family, true))
            {
                // Em 23/11/2026 nenhuma atendente conseguia abrir as Notas Autoadesivas: o Windows não tinha registrado o
                // pacote para a conta dela. O pacote costuma estar no computador (vem com o Windows); o registro para a
                // conta não precisa de administrador. Tenta uma vez por sessão do Terminal.
                if (TryRegister(family, out var error))
                {
                    path = LocateExecutable(family, appId);
                }

                if (path is null)
                {
                    Problems.Enqueue((name, error is null
                        ? "o aplicativo não está instalado nesta conta do Windows (instale pela Microsoft Store)"
                        : $"o Windows não instalou o aplicativo nesta conta: {error}"));
                }
            }

            if (path is not null)
            {
                ByExe[System.IO.Path.GetFileName(path)] = $"{family}!{appId}";
                yield return (path, name);
            }
        }
    }

    private static readonly ConcurrentDictionary<string, bool> RegisterTried = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<(string Name, string Problem)> Problems = new();

    /// <summary>Problemas achados na varredura (aplicativo que não deu para registrar), para o Terminal avisar a Central uma vez.</summary>
    public static List<(string Name, string Problem)> TakeProblems()
    {
        var list = new List<(string, string)>();
        while (Problems.TryDequeue(out var problem))
        {
            list.Add(problem);
        }

        return list;
    }

    /// <summary>
    /// Registra para a conta atual o pacote da família (que já está no computador), como o Windows faz no primeiro logon.
    /// Usa o Add-AppxPackage do Windows, escondido; espera até 2 minutos. <paramref name="error"/> traz a mensagem do Windows.
    /// </summary>
    private static bool TryRegister(string family, out string? error)
    {
        error = null;
        var powershell = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell) || family.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_')))
        {
            error = "o PowerShell do Windows não foi encontrado";
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(powershell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"[Console]::OutputEncoding = [Text.Encoding]::UTF8; Add-AppxPackage -RegisterByFamilyName -MainPackage '{family}' -ErrorAction Stop\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });
            if (process is null)
            {
                error = "o PowerShell não abriu";
                return false;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                error = "o Windows demorou mais de 2 minutos";
                return false;
            }

            if (process.ExitCode == 0)
            {
                Log.Info($"Pacote {family} registrado para a conta.");
                return true;
            }

            var message = string.Join(' ', (stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result : "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('+') && !l.StartsWith("No linha", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("At line", StringComparison.OrdinalIgnoreCase))
                .Take(3));
            error = message.Length == 0 ? $"código {process.ExitCode}" : message[..Math.Min(message.Length, 300)];
            Log.Info($"Não deu para registrar {family} para a conta: {error}");
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Caminho do executável do aplicativo <paramref name="appId"/> do pacote da família <paramref name="family"/>
    /// ("Nome_editor"), na versão mais nova registrada para a conta; <c>null</c> se não houver.
    /// </summary>
    internal static string? LocateExecutable(string family, string appId)
    {
        var separator = family.LastIndexOf('_');
        if (separator <= 0)
        {
            return null;
        }

        var prefix = family[..separator] + "_";
        var suffix = "__" + family[(separator + 1)..];

        string[] fullNames;
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
            fullNames = packages?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var candidates = fullNames
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(n => (FullName: n, Version: VersionOf(n, prefix)))
            .OrderByDescending(c => c.Version);

        foreach (var (fullName, _) in candidates)
        {
            string? root;
            try
            {
                using var package = Registry.CurrentUser.OpenSubKey(PackagesKey + "\\" + fullName);
                root = package?.GetValue("PackageRootFolder") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(root) || ExecutableFromManifest(System.IO.Path.Combine(root, "AppxManifest.xml"), appId) is not { } relative)
            {
                continue;
            }

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>Versão do nome completo "Nome_1.2.3.4_x64__editor"; zero se não der para ler.</summary>
    private static Version VersionOf(string fullName, string prefix)
    {
        var rest = fullName[prefix.Length..];
        var end = rest.IndexOf('_');
        return end > 0 && Version.TryParse(rest[..end], out var version) ? version : new Version(0, 0);
    }

    /// <summary>Atributo <c>Executable</c> do <c>&lt;Application Id="…"&gt;</c> do manifesto, ou <c>null</c>.</summary>
    private static string? ExecutableFromManifest(string manifestPath, string appId)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var document = XDocument.Load(manifestPath);
            var executable = document.Descendants()
                .Where(e => e.Name.LocalName == "Application" && string.Equals((string?)e.Attribute("Id"), appId, StringComparison.Ordinal))
                .Select(e => (string?)e.Attribute("Executable"))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            return executable?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Abre o aplicativo da Loja correspondente ao lançador. Devolve o id do processo aberto, ou <c>null</c> com o motivo
    /// (aplicativo não instalado nesta conta, por exemplo); aí quem chama volta ao lançador comum.
    /// </summary>
    public static int? Activate(string exe, out string? error)
    {
        error = null;
        if (!ByExe.TryGetValue(exe, out var aumid))
        {
            error = "não é um aplicativo da Loja";
            return null;
        }

        return ActivateAumid(aumid, out error);
    }

    /// <summary>Abre o aplicativo da Loja pelo identificador ("Família!Id"). Devolve o id do processo, ou <c>null</c> com o motivo.</summary>
    public static int? ActivateAumid(string aumid, out string? error)
    {
        error = null;
        object? manager = null;
        try
        {
            manager = new CApplicationActivationManager();
            var hr = ((IApplicationActivationManager)manager).ActivateApplication(aumid, null, ActivateOptions.None, out var processId);
            if (hr < 0)
            {
                error = $"HRESULT 0x{hr:X8}";
                return null;
            }

            return processId == 0 ? null : (int)processId;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            if (manager is not null)
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
    }

    private enum ActivateOptions
    {
        None = 0,
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class CApplicationActivationManager
    {
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, ActivateOptions options, out uint processId);

        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }
}
