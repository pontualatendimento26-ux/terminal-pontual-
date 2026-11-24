using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Lightshot (print de uma parte da tela) na conta bloqueada (24/11/2026). O Terminal segurava o PrtSc para copiar a tela
/// inteira e a tecla nunca chegava ao Lightshot; e, com o Terminal no lugar do Explorer, ninguém garante que ele abra no
/// logon. Instalado em Arquivos de Programas e não bloqueado pelo TI, o Terminal o abre e deixa o PrtSc passar para ele.
/// Sem o Lightshot rodando, o PrtSc continua copiando a tela inteira.
/// </summary>
public partial class MainWindow
{
    private bool _lightshotTried;
    private bool _snapshotDown;
    private bool _snapshotPass;

    /// <summary>Só Arquivos de Programas: a atendente não grava lá.</summary>
    private static string? LightshotPath() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(Environment.GetFolderPath)
            .Where(f => f.Length > 0)
            .Select(f => Path.Combine(f, "Skillbrains", "lightshot", "Lightshot.exe"))
            .FirstOrDefault(File.Exists);

    private bool LightshotPermitted(string path) =>
        AppPolicy.Decide(path, _config) is AppVerdict.Allowed or AppVerdict.Request;

    /// <summary>A cada configuração; abre uma vez por execução do Terminal. Antes do Explorer escondido, que disputaria o PrtSc.</summary>
    private void ApplyLightshot()
    {
        if (!_locked || _windowed || _local || _lightshotTried || _config is null
            || LightshotPath() is not { } path || !LightshotPermitted(path))
        {
            return;
        }

        _lightshotTried = true;
        FreePrintScreenKey();
        StartLightshot(path);
    }

    private void StartLightshot(string path)
    {
        if (LightshotRunning())
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
            Log.Info($"Lightshot aberto: {path}");
            if (AppPolicy.Decide(path, _config) == AppVerdict.Request)
            {
                Record(DeviceEvent.AppOpened, "Lightshot", path);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("abrir o Lightshot", ex);
            Record(DeviceEvent.AppFailed, "Lightshot", ex.Message);
        }
    }

    /// <summary>No Windows 11 o Explorer registra o PrtSc para a Ferramenta de Captura e o Lightshot não consegue registrá-lo.</summary>
    private static void FreePrintScreenKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard");
            key.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("soltar o PrtSc da Ferramenta de Captura", ex);
        }
    }

    private static bool LightshotRunning()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = false;
        foreach (var process in Process.GetProcessesByName("Lightshot"))
        {
            using (process)
            {
                try
                {
                    found |= process.SessionId == session;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Saiu durante a conferência.
                }
            }
        }

        return found;
    }

    /// <summary>Decide quem fica com o PrtSc ao apertar; soltar segue a mesma decisão. Verdadeiro: deixa passar ao Lightshot.</summary>
    private bool PassSnapshotKey(bool down)
    {
        if (down && !_snapshotDown)
        {
            var path = LightshotPath();
            // Só o PrtSc puro: Ctrl+PrtSc do Lightshot publica a tela na internet sem perguntar, e Alt+PrtSc é a cópia da janela.
            var modified = new[] { NativeMethods.VkControl, NativeMethods.VkMenu, 0x10 /* Shift */ }
                .Any(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0);
            _snapshotPass = !modified && path is not null && LightshotPermitted(path) && LightshotRunning();
            if (!_snapshotPass && !modified && path is not null && _lightshotTried && LightshotPermitted(path))
            {
                // Foi fechado: abre de novo para o próximo PrtSc; este ainda copia a tela inteira.
                Dispatcher.BeginInvoke(() => StartLightshot(path));
            }
        }

        _snapshotDown = down;
        return _snapshotPass;
    }
}
