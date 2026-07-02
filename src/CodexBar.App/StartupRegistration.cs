using Microsoft.Win32;

namespace CodexBar.App;

/// <summary>
/// Registers CodexBar to launch at Windows sign-in via the per-user Run key, so it is
/// always waiting in the tray — summoning it with the hotkey is then instant, avoiding the
/// cold process-start cost of launching from a shortcut. Best-effort: failures are ignored.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexBar";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry access can be denied; run-at-login is a convenience, never fatal.
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
