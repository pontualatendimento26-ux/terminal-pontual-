using System.IO;
using Microsoft.Win32;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Desfaz o bloqueio da CONTA ATUAL direto no registro do usuário (HKEY_CURRENT_USER).
/// Espelha o RemoverBloqueio do instalador (Bloqueio-Comum.ps1), mas só para quem está logado:
/// tudo o que o bloqueio grava fica no registro da própria conta, então não precisa de administrador.
/// Depois disto, ao entrar de novo, a conta volta a abrir a área de trabalho normal (shell = explorer.exe).
/// </summary>
internal static class Unblock
{
    private const string RestrictRunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\RestrictRun";

    // Mesma lista de PoliticasDoBloqueio (Bloqueio-Comum.ps1): chave dentro de HKCU + nome do valor a apagar.
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
    ];

    /// <summary>
    /// Remove o bloqueio da conta atual. Devolve <c>false</c> se algo não pôde ser apagado
    /// (por falta de permissão, por exemplo); mesmo assim remove tudo o que conseguir.
    /// </summary>
    public static bool RemoveForCurrentUser()
    {
        var user = Registry.CurrentUser;
        var ok = true;

        // A lista de programas liberados (RestrictRun) é uma subchave inteira.
        try
        {
            user.DeleteSubKeyTree(RestrictRunKey, throwOnMissingSubKey: false);
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
                using var sub = user.OpenSubKey(key, writable: true);
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
}
