using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexBar.App.Media;

/// <summary>
/// The volume of one app on the default speakers (Windows' per-app mixer), found from the app id
/// of its media session. A browser has several audio sessions; they move together.
/// </summary>
internal static class AppVolume
{
    /// <summary>The app's volume (0–1), or null when it has no audio session to control.</summary>
    public static float? Get(string appId)
    {
        float? volume = null;
        ForEachSession(appId, simple =>
        {
            simple.GetMasterVolume(out var level);
            volume ??= level;
        });
        return volume;
    }

    public static void Set(string appId, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var context = Guid.Empty;
        ForEachSession(appId, simple => simple.SetMasterVolume(level, ref context));
    }

    // The process names a media session's app id stands for.
    internal static string[] ProcessNames(string appId)
    {
        var id = appId.Split('!')[0].ToLowerInvariant();
        if (id.EndsWith(".exe", StringComparison.Ordinal))
        {
            id = id[..^4];
        }

        return id switch
        {
            _ when id.Contains("zunemusic") => ["microsoft.media.player"],
            "308046b0af4a39cb" => ["firefox"],
            _ when id.Contains("msedge") => ["msedge"],
            _ when id.Contains("chrome") => ["chrome"],
            _ when id.Contains("spotify") => ["spotify"],
            _ when id.Contains('_') => [id.Split('_')[0].Split('.')[^1]],
            _ => [id]
        };
    }

    private static void ForEachSession(string appId, Action<ISimpleAudioVolume> action)
    {
        if (appId.Length == 0)
        {
            return;
        }

        var names = ProcessNames(appId);
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0)
            {
                return;
            }

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var managerObject) != 0)
            {
                return;
            }

            var manager = (IAudioSessionManager2)managerObject;
            if (manager.GetSessionEnumerator(out var sessions) != 0)
            {
                return;
            }

            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0)
                {
                    continue;
                }

                try
                {
                    if (control.GetProcessId(out var pid) != 0 || pid == 0)
                    {
                        continue;
                    }

                    string name;
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        name = process.ProcessName.ToLowerInvariant();
                    }
                    catch (ArgumentException)
                    {
                        continue; // gone meanwhile
                    }

                    if (names.Any(n => name == n || name.StartsWith(n, StringComparison.Ordinal)))
                    {
                        action((ISimpleAudioVolume)control);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("AppVolume", exception.Message);
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    // ---- Core Audio COM interfaces (only the members used, in vtable order) ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

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

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
    }
}
