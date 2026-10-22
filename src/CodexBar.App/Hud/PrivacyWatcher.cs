using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodexBar.App.Hud;

/// <summary>Which apps use the microphone and the camera right now.</summary>
internal sealed record PrivacyState(IReadOnlyList<string> Microphone, IReadOnlyList<string> Camera)
{
    public static readonly PrivacyState None = new([], []);

    public bool Equivalent(PrivacyState other) =>
        Microphone.SequenceEqual(other.Microphone) && Camera.SequenceEqual(other.Camera);
}

/// <summary>
/// The microphone and camera in use, read from what Windows itself records for its privacy
/// indicator (CapabilityAccessManager\ConsentStore: an app whose LastUsedTimeStop is 0 is using it
/// now). Windows signals changes to those keys, so nothing is polled: one thread-pool wait per key.
/// </summary>
internal sealed class PrivacyWatcher : IDisposable
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private const int NotifyName = 0x1;
    private const int NotifyLastSet = 0x4;

    private readonly List<(RegistryKey Key, AutoResetEvent Signal, RegisteredWaitHandle Wait)> _watches = [];
    private readonly object _gate = new();
    private PrivacyState _state = PrivacyState.None;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread when the set of apps changes.</summary>
    public event Action<PrivacyState>? Changed;

    public PrivacyState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public PrivacyWatcher()
    {
        foreach (var capability in new[] { "microphone", "webcam" })
        {
            if (Registry.CurrentUser.OpenSubKey(Store + capability) is not { } key)
            {
                continue;
            }

            var signal = new AutoResetEvent(false);
            Arm(key, signal);
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => OnSignal(key, signal), null, Timeout.Infinite, executeOnlyOnce: false);
            _watches.Add((key, signal, wait));
        }

        _state = Read();
    }

    private void OnSignal(RegistryKey key, AutoResetEvent signal)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Arm(key, signal);   // one notification per arming
            var next = Read();
            if (next.Equivalent(_state))
            {
                return;
            }

            _state = next;
        }

        Changed?.Invoke(_state);
    }

    private static void Arm(RegistryKey key, AutoResetEvent signal) =>
        RegNotifyChangeKeyValue(key.Handle, true, NotifyName | NotifyLastSet, signal.SafeWaitHandle, true);

    private static PrivacyState Read() => new(InUse("microphone"), InUse("webcam"));

    private static List<string> InUse(string capability)
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Store + capability);
            if (root is null)
            {
                return apps;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var desktop = root.OpenSubKey(name);
                    foreach (var path in desktop?.GetSubKeyNames() ?? [])
                    {
                        using var app = desktop!.OpenSubKey(path);
                        if (Active(app))
                        {
                            apps.Add(DesktopName(path));
                        }
                    }
                }
                else
                {
                    using var app = root.OpenSubKey(name);
                    if (Active(app))
                    {
                        apps.Add(PackageName(name));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }

        apps.Sort(StringComparer.OrdinalIgnoreCase);
        return apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Active(RegistryKey? app) =>
        app?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
        app.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    // "C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom"
    internal static string DesktopName(string key) =>
        System.IO.Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));

    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"
    internal static string PackageName(string key)
    {
        var name = key.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var (key, signal, wait) in _watches)
            {
                wait.Unregister(null);
                signal.Dispose();
                key.Dispose();
            }

            _watches.Clear();
        }
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle evt, bool asynchronous);
}

/// <summary>Mutes or unmutes the default microphone (Windows' own capture mute, as in Sound settings).</summary>
internal static class MicMute
{
    /// <summary>Flip the mute; returns the new state, or null when there is no microphone.</summary>
    public static bool? Toggle() => Use(volume =>
    {
        volume.GetMute(out var muted);
        volume.SetMute(!muted, IntPtr.Zero);
        return !muted;
    });

    public static bool? IsMuted() => Use(volume =>
    {
        volume.GetMute(out var muted);
        return muted;
    });

    private static bool? Use(Func<IAudioEndpointVolume, bool> action)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.GetDefaultAudioEndpoint(1 /* capture */, 2 /* communications */, out var device) != 0)
            {
                return null;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var result) != 0)
            {
                return null;
            }

            return action((IAudioEndpointVolume)result);
        }
        catch (COMException)
        {
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

    // Members in vtable order up to GetMute (IIDs and order from endpointvolume.h).
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
