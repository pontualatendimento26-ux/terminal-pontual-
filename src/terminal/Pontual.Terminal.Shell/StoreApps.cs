using System.Runtime.InteropServices;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Aplicativos do Windows que, no Windows 11, são aplicativos da Loja: o <c>calc.exe</c> da pasta System32 é só um lançador,
/// que pede ao Windows para abrir a Calculadora e sai na hora. Na conta bloqueada da atendente esse pedido do lançador não
/// chega a abrir nada (conferido em 20/11/2026: "Abriu Calculadora" e "Fechou Calculadora" um segundo depois, sem janela).
/// Aqui o Terminal pede a abertura direto ao gerenciador de ativação do Windows, pelo identificador do aplicativo, e recebe
/// o processo aberto, que entra em "Abertos agora" como qualquer outro.
/// </summary>
internal static class StoreApps
{
    /// <summary>Identificador (AUMID) do aplicativo da Loja que cada lançador da pasta System32 abre.</summary>
    private static readonly Dictionary<string, string> ByExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calc.exe"] = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        ["notepad.exe"] = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
        ["mspaint.exe"] = "Microsoft.Paint_8wekyb3d8bbwe!App",
        ["snippingtool.exe"] = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
        ["stikynot.exe"] = "Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App",
    };

    public static bool IsStoreLauncher(string exe) => ByExe.ContainsKey(exe);

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
