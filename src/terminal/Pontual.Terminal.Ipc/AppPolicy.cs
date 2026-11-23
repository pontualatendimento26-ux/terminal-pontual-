namespace Pontual.Terminal.Ipc;

/// <summary>O que o Terminal faz quando a atendente pede para abrir um aplicativo do computador.</summary>
public enum AppVerdict
{
    /// <summary>Programa cadastrado pelo TI para a máquina, ou aplicativo já liberado: abre sem avisar.</summary>
    Allowed,

    /// <summary>Aplicativo que o TI ainda não decidiu: abre, e a Central recebe o pedido de liberação.</summary>
    Request,

    /// <summary>Aplicativo que o TI bloqueou: não abre (a tentativa vai para a Central).</summary>
    Blocked,

    /// <summary>Nunca abre pela lista de aplicativos (ferramentas do sistema, atalhos que não são .exe).</summary>
    Refused,
}

/// <summary>
/// Regras da lista "Aplicativos" do Terminal (docs/APLICATIVOS-DO-COMPUTADOR.md). A atendente pode abrir os aplicativos
/// do Windows, os instalados e os baixados; a decisão do TI é por nome do arquivo (<c>spotify.exe</c>), porque o caminho
/// muda de um computador para outro. Classe pura: não conhece o disco nem a rede.
/// </summary>
public static class AppPolicy
{
    /// <summary>
    /// Além dos recusados do contrato (<see cref="ProgramPolicy.RefusedNames"/>): ferramentas de administração do
    /// Windows que a atendente não abre pela lista, mesmo que o TI não tenha bloqueado. O TI continua podendo
    /// cadastrá-las como programa da máquina, se precisar.
    /// </summary>
    public static readonly IReadOnlySet<string> RefusedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr", "control", "mmc", "msconfig", "regedt32", "gpedit", "secpol", "lusrmgr", "compmgmt", "services",
        "eventvwr", "perfmon", "resmon", "netsh", "reg", "sc", "schtasks", "certutil", "bitsadmin", "powershell_ise",
        "conhost", "diskpart", "wmic", "cipher", "dism", "bcdedit", "takeown", "icacls", "runas", "psexec", "net", "net1",
        "ftp", "telnet", "tscon", "msra", "winrs", "wbadmin", "vssadmin", "explorer", "winlogon", "userinit", "logonui",
        "sethc", "utilman", "narrator", "osk", "magnify", "displayswitch", "cmdkey", "at", "shutdown", "logoff", "tsdiscon",
        // Interpretadores e ferramentas de desenvolvedor: rodam qualquer código. O TI cadastra como programa se precisar.
        "git-bash", "git-cmd", "sh", "python", "pythonw", "py", "node", "ruby", "perl", "java", "javaw", "ssh", "scp", "sftp",
        "curl", "wget", "mintty", "appverif", "windbg", "procexp", "procexp64", "procmon", "procmon64", "autoruns", "nircmd",
        // Aplicativos da Loja que abrem um console, outro computador ou as configurações do Windows.
        "windowsterminal", "openconsole", "wsl", "wslhost", "ubuntu", "debian", "kali", "winget", "appinstaller",
        "systemsettings", "quickassist", "msrdc", "mstsc", "powertoys", "powertoys.run", "devhome", "microsoft.cmdpal.ui",
    };

    /// <summary>Nome do arquivo em minúsculas ("spotify.exe"), ou <c>null</c> se o caminho não indica um .exe.</summary>
    public static string? ExeName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var clean = path.Trim();
        var start = clean.LastIndexOfAny(['\\', '/']) + 1;
        var name = clean[start..].Trim();
        if (name.Length < 5 || name.Length > 120 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.IndexOf(':') >= 0)
        {
            return null;
        }

        return name.ToLowerInvariant();
    }

    /// <summary>Verdadeiro para o que nunca abre pela lista: não é .exe, é recusado pelo contrato ou é ferramenta do sistema.</summary>
    public static bool IsRefused(string? path)
    {
        if (ProgramPolicy.IsRefused(path) || ExeName(path) is not { } exe)
        {
            return true;
        }

        var stem = exe[..^4].Trim(' ', '.');
        return stem.Length == 0 || RefusedNames.Contains(stem) || stem.StartsWith("pontual.terminal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Programas clássicos da Microsoft (Office, Teams, OneDrive) que já vêm liberados (pedido do dono em 23/11/2026:
    /// "libere o acesso a todos os apps da microsoft"). Só contam quando estão em Arquivos de Programas, que a atendente
    /// não consegue gravar; quem confere a pasta é o catálogo do Terminal.
    /// </summary>
    public static readonly IReadOnlySet<string> MicrosoftExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe", "msaccess.exe", "mspub.exe",
        "visio.exe", "winproj.exe", "lync.exe", "teams.exe", "ms-teams.exe", "olk.exe", "onedrive.exe",
    };

    /// <summary>
    /// O veredito para um caminho, segundo a configuração atual da máquina. <paramref name="microsoft"/>: aplicativo da
    /// Microsoft (da Loja ou do Office), que abre liberado; o bloqueio do TI continua valendo mais.
    /// </summary>
    public static AppVerdict Decide(string? path, TerminalConfig? config, bool microsoft = false)
    {
        if (path is null || IsRefused(path) || !Path.IsPathFullyQualified(path))
        {
            return AppVerdict.Refused;
        }

        if (config is null)
        {
            return microsoft ? AppVerdict.Allowed : AppVerdict.Request;
        }

        var exe = ExeName(path)!;
        if (Matches(exe, config.AppsBlocked))
        {
            return AppVerdict.Blocked;
        }

        if (microsoft || Matches(exe, config.AppsAllowed) || config.Items.Any(i => i.IsProgram && string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase)))
        {
            return AppVerdict.Allowed;
        }

        return AppVerdict.Request;
    }

    private static bool Matches(string exe, IReadOnlyList<string>? list) =>
        list is not null && list.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase));
}
