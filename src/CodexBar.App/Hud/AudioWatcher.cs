using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CodexBar.App.Hud;

/// <summary>
/// Follows the default speakers through Core Audio: their master volume and mute (whatever moved
/// them — the keyboard's volume keys, the tray, an app), and which device is the default, so a
/// pair of headphones becoming the output can be shown. Events arrive on the UI thread.
/// </summary>
internal sealed class AudioWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _volumeCallback;
    private DeviceCallback? _deviceCallback;
    private string _defaultId = string.Empty;

    public AudioWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Volume (0–1) and mute, after any change.</summary>
    public event Action<float, bool>? VolumeChanged;

    /// <summary>A new default output that is a pair of headphones or a headset (its name).</summary>
    public event Action<string>? HeadphonesConnected;

    public void Start()
    {
        try
        {
            _enumerator = CreateEnumerator();
            _deviceCallback = new DeviceCallback(this);
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            Attach();
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Audio", exception.Message);
        }
    }

    // (Re)bind to the current default speakers' volume.
    private void Attach()
    {
        Detach();
        if (_enumerator is null || _enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0)
        {
            return;
        }

        try
        {
            device.GetId(out var idPointer);
            _defaultId = Marshal.PtrToStringUni(idPointer) ?? string.Empty;
            Marshal.FreeCoTaskMem(idPointer);
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var volume) == 0)
            {
                _volume = (IAudioEndpointVolume)volume;
                _volumeCallback = new VolumeCallback(this);
                _volume.RegisterControlChangeNotify(_volumeCallback);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Detach()
    {
        if (_volume is not null)
        {
            try
            {
                if (_volumeCallback is not null)
                {
                    _volume.UnregisterControlChangeNotify(_volumeCallback);
                }
            }
            catch (COMException)
            {
            }

            Marshal.ReleaseComObject(_volume);
            _volume = null;
            _volumeCallback = null;
        }
    }

    private void OnVolume(float level, bool muted) =>
        _dispatcher.BeginInvoke(() => VolumeChanged?.Invoke(level, muted));

    private void OnDefaultChanged(string id)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (id == _defaultId)
            {
                return;
            }

            Attach();
            if (_enumerator is not null && _enumerator.GetDevice(id, out var device) == 0)
            {
                try
                {
                    var (formFactor, name) = Describe(device);
                    if (formFactor is 3 /* headphones */ or 5 /* headset */)
                    {
                        HeadphonesConnected?.Invoke(name);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        });
    }

    // The endpoint's form factor (speakers, headphones…) and its friendly name.
    private static (int FormFactor, string Name) Describe(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0)
        {
            return (-1, string.Empty);
        }

        try
        {
            var formKey = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
            var nameKey = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            var form = -1;
            var name = string.Empty;
            if (store.GetValue(ref formKey, out var formValue) == 0)
            {
                if (formValue.Type == 19 /* VT_UI4 */)
                {
                    form = (int)(formValue.Data.ToInt64() & 0xFFFFFFFF);
                }

                PropVariantClear(ref formValue);
            }

            if (store.GetValue(ref nameKey, out var nameValue) == 0)
            {
                if (nameValue.Type == 31 /* VT_LPWSTR */)
                {
                    name = Marshal.PtrToStringUni(nameValue.Data) ?? string.Empty;
                }

                PropVariantClear(ref nameValue);
            }

            return (form, name);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public void Dispose()
    {
        Detach();
        if (_enumerator is not null)
        {
            try
            {
                if (_deviceCallback is not null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                }
            }
            catch (COMException)
            {
            }

            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ---- Callbacks (called on Core Audio's threads) -------------------------------------------

    private sealed class VolumeCallback(AudioWatcher owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context (16), BOOL muted (4), float master volume (4).
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner.OnVolume(level, muted);
            return 0;
        }
    }

    private sealed class DeviceCallback(AudioWatcher owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == 0 /* render */ && role == 1 /* multimedia */ && defaultDeviceId is { Length: > 0 } id)
            {
                owner.OnDefaultChanged(id);
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    // ---- Core Audio COM interfaces (members in vtable order; IIDs from the Windows SDK headers) ----

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Data;
        public IntPtr Data2;
    }

    // Created untyped (by CLSID), so every part of the app that talks to the shared device enumerator
    // sees the same plain COM object and reaches it through its own interface declarations.
    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId(out IntPtr id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    }
}
