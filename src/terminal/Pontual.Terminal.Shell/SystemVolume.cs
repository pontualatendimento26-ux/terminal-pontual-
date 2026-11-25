using System.Runtime.InteropServices;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Volume da saída de som padrão do Windows (fone ou caixa de som). No modo bloqueado não há a barra de tarefas
/// do Windows, então é por aqui que a atendente aumenta, diminui ou silencia o som.
/// </summary>
internal static class SystemVolume
{
    /// <summary>Volume de 0 a 100 e se está silenciado; <c>null</c> quando o computador não tem saída de som.</summary>
    public static (int Percent, bool Muted)? Read() => Use<(int, bool)?>(volume =>
    {
        if (volume.GetMasterVolumeLevelScalar(out var level) != 0 || volume.GetMute(out var muted) != 0)
        {
            return null;
        }

        return ((int)Math.Round(Math.Clamp(level, 0f, 1f) * 100), muted);
    });

    /// <summary>Muda o volume. Acima de zero, tira também o mudo (como o controle do Windows).</summary>
    public static bool SetPercent(int percent) => Use(volume =>
    {
        var context = Guid.Empty;
        var value = Math.Clamp(percent, 0, 100);
        if (volume.SetMasterVolumeLevelScalar(value / 100f, ref context) != 0)
        {
            return false;
        }

        if (value > 0)
        {
            volume.SetMute(false, ref context);
        }

        return true;
    });

    public static bool SetMuted(bool muted) => Use(volume =>
    {
        var context = Guid.Empty;
        return volume.SetMute(muted, ref context) >= 0;
    });

    private static T? Use<T>(Func<IAudioEndpointVolume, T?> action)
    {
        object? enumerator = null;
        IMMDevice? device = null;
        object? endpoint = null;
        try
        {
            enumerator = new MMDeviceEnumerator();

            // Sem fone nem caixa de som ligados não há saída padrão: não é erro, só não há o que ajustar.
            if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device) != 0 || device is null)
            {
                return default;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out endpoint) != 0 || endpoint is null)
            {
                return default;
            }

            return action((IAudioEndpointVolume)endpoint);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Log.Error("volume", ex);
            return default;
        }
        finally
        {
            Release(endpoint);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null)
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 23;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    // As interfaces seguem a ordem exata dos métodos no Windows; os que o Terminal não usa ficam só para ocupar o lugar.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid context);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
