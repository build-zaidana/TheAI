using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using CodexBar.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CodexBar.App;

/// <summary>
/// "Done" feedback: a native Windows notification with <b>Lihat</b> and <b>Salin jawaban</b>
/// buttons, and a soft sound. Unpackaged apps need an AppUserModelID registered for the user
/// (display name + icon) before Windows shows their toasts; that is done once at start-up.
/// Focus Assist / Do Not Disturb and per-app notification settings are left to Windows.
/// </summary>
internal sealed partial class CompletionNotifier
{
    private const string AppId = "CodexBar.App";
    private const int MaxLiveToasts = 8;

    // Toasts keep their Activated handler only while referenced.
    private readonly List<ToastNotification> _live = new();
    private ToastNotifier? _notifier;

    /// <summary>action ("view" or "copy") and the activity key the toast belongs to.</summary>
    public event Action<string, string>? Activated;

    public void Initialize()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                key.SetValue("DisplayName", "CodexBar");
                if (WriteIcon() is { } iconPath)
                {
                    key.SetValue("IconUri", iconPath);
                }
            }

            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception exception)
        {
            // Old Windows builds or locked-down machines: fall back to the sound only.
            DiagnosticsLog.Error("Notifier", exception.Message);
            _notifier = null;
        }
    }

    /// <summary>Show a completion toast; false when Windows won't show it (disabled or unavailable).</summary>
    public bool Show(string title, string body, string activityKey, bool withSound)
    {
        if (_notifier is null)
        {
            return false;
        }

        try
        {
            var key = SecurityElement.Escape(activityKey);
            var xml = $"""
                <toast launch="action=view;key={key}" activationType="foreground">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                  <actions>
                    <action content="{SecurityElement.Escape(Tr.Text("Lihat", "View"))}" arguments="action=view;key={key}" activationType="foreground" />
                    <action content="{SecurityElement.Escape(Tr.Text("Salin jawaban", "Copy answer"))}" arguments="action=copy;key={key}" activationType="foreground" />
                  </actions>
                  <audio src="ms-winsoundevent:Notification.Default" silent="{(withSound ? "false" : "true")}" />
                </toast>
                """;
            var document = new XmlDocument();
            document.LoadXml(xml);
            var toast = new ToastNotification(document)
            {
                // A newer answer of the same conversation replaces the older toast.
                Tag = activityKey.Length > 16 ? activityKey[..16] : activityKey,
                Group = "done"
            };
            toast.Activated += Toast_Activated;
            _live.Add(toast);
            if (_live.Count > MaxLiveToasts)
            {
                _live.RemoveAt(0);
            }

            _notifier.Show(toast);
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Notifier", $"{exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return false;
        }
    }

    public static void PlaySound()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Messaging.wav");
            if (File.Exists(path))
            {
                using var player = new SoundPlayer(path);
                player.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Notifier", exception.Message);
        }
    }

    /// <summary>First lines of an answer as plain text, short enough for a notification.</summary>
    public static string Preview(string markdown, int max = 180)
    {
        var text = CodeFence().Replace(markdown ?? string.Empty, " ");
        text = MarkdownMarks().Replace(text, string.Empty);
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private void Toast_Activated(ToastNotification sender, object args)
    {
        if (args is not ToastActivatedEventArgs activated)
        {
            return;
        }

        string action = "view", key = string.Empty;
        foreach (var part in activated.Arguments.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "action")
            {
                action = pair[1];
            }
            else if (pair.Length == 2 && pair[0] == "key")
            {
                key = pair[1];
            }
        }

        Activated?.Invoke(action, key);
    }

    private static string? WriteIcon()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "notification-icon.png");
            if (!File.Exists(path) &&
                System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/codexbar.ico")) is { } resource)
            {
                using var stream = resource.Stream;
                using var icon = new System.Drawing.Icon(stream, 256, 256);
                using var bitmap = icon.ToBitmap();
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            return File.Exists(path) ? path : null;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Notifier", exception.Message);
            return null;
        }
    }

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"[*_`#>\[\]]")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
