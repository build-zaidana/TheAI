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
    public static Task<int?> GetBrightnessAsync() => OnSta<int?>(service =>
    {
        foreach (dynamic item in service.ExecQuery("SELECT * FROM WmiMonitorBrightness"))
        {
            return Convert.ToInt32(item.CurrentBrightness);
        }

        return null;
    });

    public static Task<bool> SetBrightnessAsync(int percent) => OnSta(service =>
    {
        var level = Math.Clamp(percent, 0, 100);
        foreach (dynamic item in service.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
        {
            item.WmiSetBrightness(1, level);
            return true;
        }

        return false;
    });

    private static Task<T?> OnSta<T>(Func<dynamic, T?> work)
    {
        var done = new TaskCompletionSource<T?>();
        var thread = new Thread(() =>
        {
            try
            {
                var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType is null)
                {
                    done.SetResult(default);
                    return;
                }

                dynamic locator = Activator.CreateInstance(locatorType)!;
                done.SetResult(work(locator.ConnectServer(".", @"root\wmi")));
            }
            catch (Exception exception)
            {
                CodexBar.Core.DiagnosticsLog.Error("Brightness", exception.Message);
                done.SetResult(default);
            }
        }) { IsBackground = true, Name = "Brightness" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
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
