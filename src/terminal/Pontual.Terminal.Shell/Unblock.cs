using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Desfaz o bloqueio de UMA conta, apagando no registro dela as mesmas travas que o instalador grava
/// (lista igual a PoliticasDoBloqueio, em Bloqueio-Comum.ps1).
///
/// Essas travas ficam em chaves de "política", que o Windows só deixa um ADMINISTRADOR apagar. Por isso
/// a remoção roda no processo ELEVADO que o Terminal relança com <c>--desbloquear &lt;sid&gt;</c>
/// (ver App.OnStartup e <see cref="StartElevatedRemoval"/>), agindo sobre HKEY_USERS\&lt;sid&gt; da conta logada.
/// Depois disto, ao entrar de novo, a conta volta a abrir a área de trabalho normal (shell = explorer.exe).
/// </summary>
internal static class Unblock
{
    private const string RestrictRunRelative =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\RestrictRun";

    // Mesma lista de PoliticasDoBloqueio (Bloqueio-Comum.ps1): chave (dentro do registro da conta) + valor a apagar.
    private static readonly (string Key, string Value)[] Policies =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Shell"),
        (@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableChangePassword"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools"),
        (@"Software\Policies\Microsoft\Windows\System", "DisableCMD"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "RestrictRun"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRun"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoWinKeys"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoControlPanel"),
        (@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun"),
        (@"Software\Policies\Microsoft\Edge", "HideFirstRunExperience"),
        (@"Software\Policies\Microsoft\Edge\URLBlocklist", "1"),
        (@"Software\Policies\Microsoft\Edge\URLAllowlist", "1"),
        (@"Software\Policies\Microsoft\Office\16.0\Excel\Security", "VBAWarnings"),
        (@"Software\Policies\Google\Chrome", "IncognitoModeAvailability"),
        (@"Software\Policies\Google\Chrome", "BrowserGuestModeEnabled"),
        (@"Software\Policies\Google\Chrome", "DeveloperToolsAvailability"),
    ];

    /// <summary>SID da conta logada agora (para a remoção elevada achar o registro certo em HKEY_USERS).</summary>
    public static string? CurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Error("sid da conta", ex);
            return null;
        }
    }

    /// <summary>
    /// Remove o bloqueio da conta de SID <paramref name="sid"/>, em HKEY_USERS\&lt;sid&gt;.
    /// Precisa estar num processo ELEVADO (as chaves de política só aceitam escrita de administrador).
    /// Devolve <c>false</c> se algo não pôde ser apagado; mesmo assim remove tudo o que conseguir.
    /// </summary>
    public static bool RemoveForUser(string sid)
    {
        var users = Registry.Users;
        var ok = true;

        // A lista de programas liberados (RestrictRun) e a de sites bloqueados no Chrome são subchaves inteiras.
        try
        {
            users.DeleteSubKeyTree($@"{sid}\{RestrictRunRelative}", throwOnMissingSubKey: false);
            users.DeleteSubKeyTree($@"{sid}\{ChromePolicy.BlocklistKey}", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("desbloquear RestrictRun", ex);
            ok = false;
        }

        foreach (var (key, value) in Policies)
        {
            try
            {
                using var sub = users.OpenSubKey($@"{sid}\{key}", writable: true);
                sub?.DeleteValue(value, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Log.Error($"desbloquear {key}", ex);
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Pede a permissão de administrador e remove o bloqueio desta conta: relança o próprio Terminal
    /// com <c>--desbloquear &lt;sid&gt;</c> (janela do Windows pedindo a permissão). Esse processo elevado
    /// apaga as travas e sai da conta. Devolve <c>false</c> se o usuário cancelou a permissão.
    /// </summary>
    public static bool StartElevatedRemoval()
    {
        var sid = CurrentUserSid();
        var exe = Environment.ProcessPath;
        if (sid is null || exe is null)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--desbloquear {sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 (ERROR_CANCELLED): o usuário fechou/recusou a janela de permissão.
            Log.Error("elevar desbloqueio", ex);
            return false;
        }
    }
}
