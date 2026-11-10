using System.Runtime.InteropServices;
using Windows.Devices.Radios;

namespace CodexBar.App.Hud;

/// <summary>
/// Reads and sets what the Control Center page controls: speaker volume (Core Audio), the built-in
/// panel's brightness (WMI) and the Wi-Fi / Bluetooth radios. Every call fails soft (null / false)
/// when the machine has no such device, e.g. no built-in panel on a desktop.
/// </summary>
internal static class SystemControls
{
    // ---- volume
    public static int? GetVolume() => WithVolume(v => v.GetMasterVolumeLevelScalar(out var level) == 0 ? (int?)Math.Round(level * 100) : null);

    public static bool SetVolume(int percent) =>
        WithVolume(v => (int?)v.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, IntPtr.Zero)) == 0;

    private static T? WithVolume<T>(Func<IAudioEndpointVolume, T?> action)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0)
            {
                return default;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, 23, IntPtr.Zero, out var result) == 0 ? action((IAudioEndpointVolume)result) : default;
        }
        catch (COMException)
        {
            return default;
        }
    }

    // ---- brightness (WMI scripting objects are apartment-threaded, so each call gets its own STA thread)
    // System.Management, not late-bound COM: through "dynamic", each WMI query worked once per process
    // and then failed with "Unspecified error", which left the slider dead.
    public static Task<int?> GetBrightnessAsync() => Task.Run<int?>(() =>
    {
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var found = search.Get();
            foreach (var item in found)
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"]);
                }
            }
        }
        catch (Exception exception) when (exception is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            CodexBar.Core.DiagnosticsLog.Error("Brightness", exception.Message);
        }

        return null;
    });

    private static int _brightnessWanted = -1;

    /// <summary>A slider sends a value per pointer move: while one is being applied only the newest waits.</summary>
    public static Task<bool> SetBrightnessAsync(int percent)
    {
        var alreadyWaiting = Interlocked.Exchange(ref _brightnessWanted, Math.Clamp(percent, 0, 100)) >= 0;
        return alreadyWaiting ? Task.FromResult(true) : Task.Run(() =>
        {
            var applied = false;
            try
            {
                using var search = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var found = search.Get();
                var level = Interlocked.Exchange(ref _brightnessWanted, -1);
                foreach (System.Management.ManagementObject item in found)
                {
                    using (item)
                    {
                        item.InvokeMethod("WmiSetBrightness", [1u, (byte)level]);
                        applied = true;
                    }

                    break;
                }
            }
            catch (Exception exception) when (exception is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                CodexBar.Core.DiagnosticsLog.Error("Brightness", exception.Message);
            }

            return applied;
        });
    }

    // ---- radios
    /// <summary>On / off of the first radio of this kind; null when the PC has none or access is denied.</summary>
    public static async Task<bool?> GetRadioAsync(RadioKind kind) => await FindRadio(kind) is { } radio ? radio.State == RadioState.On : null;

    public static async Task<bool?> ToggleRadioAsync(RadioKind kind)
    {
        if (await FindRadio(kind) is not { } radio)
        {
            return null;
        }

        var want = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        return await radio.SetStateAsync(want) == RadioAccessStatus.Allowed ? want == RadioState.On : null;
    }

    private static async Task<Radio?> FindRadio(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                return null;
            }

            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Radios", exception.Message);
            return null;
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }
}
