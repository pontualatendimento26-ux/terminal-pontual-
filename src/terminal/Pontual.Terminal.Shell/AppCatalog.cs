using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>Um aplicativo do computador que a atendente pode pedir para abrir.</summary>
/// <param name="Source"><c>windows</c> (vem com o Windows), <c>installed</c> (atalho do menu Iniciar) ou <c>downloaded</c> (pasta Downloads).</param>
internal sealed record AppEntry(string Path, string Name, string Exe, string Source);

/// <summary>
/// Lista os aplicativos do computador para a tela "Aplicativos" do Terminal: os do Windows (Calculadora, Bloco de Notas…),
/// os instalados (atalhos .lnk do menu Iniciar, de todos os usuários e da conta) e os baixados (.exe na pasta Downloads).
/// O que o contrato recusa e as ferramentas de administração (<see cref="AppPolicy.IsRefused"/>) não entram.
/// Só lê o disco; não abre nada.
/// </summary>
internal static class AppCatalog
{
    private const int MaxApps = 400;

    private static readonly (string File, string Name)[] WindowsApps =
    [
        ("calc.exe", "Calculadora"),
        ("notepad.exe", "Bloco de Notas"),
        ("mspaint.exe", "Paint"),
        ("SnippingTool.exe", "Ferramenta de Captura"),
        ("write.exe", "WordPad"),
        ("charmap.exe", "Mapa de Caracteres"),
        ("StikyNot.exe", "Notas Autoadesivas"),
    ];

    private static readonly string[] UninstallerWords = ["uninstall", "desinstal", "remover", "remove ", "unins"];

    /// <summary>Varre fora da linha da interface, numa linha STA (a leitura dos atalhos usa COM do Windows).</summary>
    public static Task<List<AppEntry>> ScanAsync(string downloadsFolder)
    {
        var completion = new TaskCompletionSource<List<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Scan(downloadsFolder));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Pontual.AppCatalog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static List<AppEntry> Scan(string downloadsFolder)
    {
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (file, name) in WindowsApps)
        {
            Add(found, Path.Combine(system, file), name, "windows");
        }

        // Notas Autoadesivas: sem lançador na System32 desde o Windows 10 1607, só o pacote da Loja. O executável do pacote
        // registrado para a conta entra como aplicativo do Windows; a abertura é pelo gerenciador de ativação (StoreApps).
        foreach (var (path, name) in StoreApps.InstalledWithoutLauncher())
        {
            Add(found, path, name, "windows");
        }

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            foreach (var shortcut in Enumerate(root, "*.lnk", recurse: true))
            {
                // Da pasta do Windows só entram os aplicativos da lista fixa acima: os atalhos "Ferramentas Administrativas",
                // "Sistema do Windows" e "Acessibilidade" (limpeza de disco, ODBC, lupa…) não são para a atendente.
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (ShellLink.Resolve(shortcut) is not { } target || IsUninstaller(name, target)
                    || target.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(found, target, name, "installed");
            }
        }

        foreach (var exe in Enumerate(downloadsFolder, "*.exe", recurse: false))
        {
            Add(found, exe, Path.GetFileNameWithoutExtension(exe), "downloaded");
        }

        return [.. found.Values.OrderBy(a => a.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true)).Take(MaxApps)];
    }

    private static void Add(Dictionary<string, AppEntry> found, string path, string name, string source)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return;
        }

        if (found.ContainsKey(full) || AppPolicy.IsRefused(full) || AppPolicy.ExeName(full) is not { } exe || !File.Exists(full))
        {
            return;
        }

        var clean = name.Trim();
        found[full] = new AppEntry(full, clean.Length == 0 ? exe : clean[..Math.Min(clean.Length, 60)], exe, source);
    }

    private static bool IsUninstaller(string shortcutName, string target)
    {
        var lower = shortcutName.ToLowerInvariant();
        var file = Path.GetFileName(target).ToLowerInvariant();
        return UninstallerWords.Any(w => lower.Contains(w, StringComparison.Ordinal)) || file.StartsWith("unins", StringComparison.Ordinal);
    }

    private static IEnumerable<string> Enumerate(string? folder, string pattern, bool recurse)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            }).Take(2000).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Lê o destino de um atalho .lnk pela interface IShellLink do Windows. Devolve <c>null</c> para o que não é um .exe.</summary>
internal static class ShellLink
{
    private const uint StgmRead = 0;
    private const uint SlgpRawPath = 4;

    public static string? Resolve(string shortcutPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(shortcutPath, StgmRead);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString().Trim());
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(target) ? target : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
