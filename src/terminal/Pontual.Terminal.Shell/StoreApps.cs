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

    /// <summary>
    /// Os aplicativos de <see cref="WithoutLauncher"/> instalados para a conta atual: caminho do executável dentro do pacote
    /// e nome. Só lê registro e disco; o que não estiver registrado para a conta fica de fora (e não abriria mesmo).
    /// </summary>
    public static IEnumerable<(string Path, string Name)> InstalledWithoutLauncher()
    {
        foreach (var (family, appId, name) in WithoutLauncher)
        {
            if (LocateExecutable(family, appId) is { } path)
            {
                ByExe[System.IO.Path.GetFileName(path)] = $"{family}!{appId}";
                yield return (path, name);
            }
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
