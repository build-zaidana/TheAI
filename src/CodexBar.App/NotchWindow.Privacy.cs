using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.Hud;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

// The microphone and camera indicator, mic mute, and prayer times.
//  - A dot at the notch's edge while an app uses the microphone (orange) or the camera (green), and
//    a short HUD naming the app when that starts. Driven by Windows' own registry signal; off unless
//    chosen.
//  - /mute (and the Today page's row while the microphone is in use) mutes the default microphone.
//  - Prayer times for the weather city, worked out on the PC: the Today page and the weather page
//    show the next one, and a quiet HUD says when it begins. One timer, set for the next prayer.
public partial class NotchWindow
{
    private static readonly Geometry MicGlyph = Geometry.Parse("M 9,2 A 3,3 0 0 1 12,5 L 12,9 A 3,3 0 0 1 6,9 L 6,5 A 3,3 0 0 1 9,2 Z M 3.5,8.5 A 5.5,5.5 0 0 0 14.5,8.5 M 9,14 L 9,16.5");
    private static readonly Geometry MicOffGlyph = Geometry.Parse("M 9,2 A 3,3 0 0 1 12,5 L 12,9 A 3,3 0 0 1 6,9 L 6,5 A 3,3 0 0 1 9,2 Z M 3.5,8.5 A 5.5,5.5 0 0 0 14.5,8.5 M 9,14 L 9,16.5 M 2,2 L 16,16");
    private static readonly Geometry CameraGlyph = Geometry.Parse("M 2,5 L 12,5 L 12,13 L 2,13 Z M 12,8 L 16,5.5 L 16,12.5 L 12,10");
    private static readonly Geometry CrescentGlyph = Geometry.Parse("M 12,2.5 A 7,7 0 1 0 15.5,14 A 5.5,5.5 0 0 1 12,2.5 Z");
    private static readonly Brush MicDotBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush CameraDotBrush = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));

    private readonly DispatcherTimer _prayerTimer = new();
    private PrivacyWatcher? _privacy;
    private PrivacyState _privacyShown = PrivacyState.None;
    private bool _privacyPreview;

    private bool PrayerAvailable => _host.PrayerTimes && _place is not null;

    private void InitPrivacy()
    {
        _prayerTimer.Tick += (_, _) => AnnouncePrayer();
        Closed += (_, _) =>
        {
            _privacy?.Dispose();
            _prayerTimer.Stop();
        };
        ApplyPrivacyWatcher();
    }

    /// <summary>Settings may have turned the indicator on or off.</summary>
    private void ApplyPrivacyWatcher()
    {
        if (_host.PrivacyDot && _privacy is null)
        {
            _privacy = new PrivacyWatcher();
            _privacy.Changed += state => Dispatcher.BeginInvoke(() => ShowPrivacy(state, announce: true));
            ShowPrivacy(_privacy.State, announce: false);
        }
        else if (!_host.PrivacyDot && _privacy is not null && !_privacyPreview)
        {
            _privacy.Dispose();
            _privacy = null;
            ShowPrivacy(PrivacyState.None, announce: false);
        }
    }

    private void ShowPrivacy(PrivacyState state, bool announce)
    {
        var before = _privacyShown;
        _privacyShown = state;
        var mic = state.Microphone.Count > 0;
        var camera = state.Camera.Count > 0;
        PrivacyDot.Visibility = mic || camera ? Visibility.Visible : Visibility.Collapsed;
        PrivacyDot.Fill = camera ? CameraDotBrush : MicDotBrush;
        PrivacyDot.ToolTip = string.Join("\n", new[]
        {
            mic ? Tr.Text("Mikrofon: ", "Microphone: ") + string.Join(", ", state.Microphone) : string.Empty,
            camera ? Tr.Text("Kamera: ", "Camera: ") + string.Join(", ", state.Camera) : string.Empty,
        }.Where(line => line.Length > 0));

        if (!announce || Quiet || _fullscreenHidden)
        {
            return;
        }

        // Say it once, when an app starts using it.
        if (state.Camera.Except(before.Camera).FirstOrDefault() is { } cameraApp)
        {
            ShowTextHud(CameraGlyph, Tr.Text("Kamera dipakai · ", "Camera in use · ") + cameraApp, red: false);
        }
        else if (state.Microphone.Except(before.Microphone).FirstOrDefault() is { } micApp)
        {
            ShowTextHud(MicGlyph, Tr.Text("Mikrofon dipakai · ", "Microphone in use · ") + micApp, red: false);
        }
    }

    private void ToggleMicMute()
    {
        var muted = MicMute.Toggle();
        if (muted is null)
        {
            ShowTextHud(MicOffGlyph, Tr.Text("Tidak ada mikrofon", "No microphone"), red: false);
            return;
        }

        ShowTextHud(muted.Value ? MicOffGlyph : MicGlyph,
            muted.Value ? Tr.Text("Mikrofon dibisukan", "Microphone muted") : Tr.Text("Mikrofon nyala", "Microphone on"), red: muted.Value);
        if (_view == NotchView.Today)
        {
            RenderToday();
        }
    }

    // ---- Prayer times ----------------------------------------------------------------------

    private Prayer? NextPrayer(DateTimeOffset now) =>
        PrayerAvailable ? PrayerTimes.Next(now, _place!.Latitude, _place.Longitude) : null;

    /// <summary>Set the one timer for the next prayer (the city or the setting may have changed).</summary>
    private void SchedulePrayer()
    {
        _prayerTimer.Stop();
        if (NextPrayer(DateTimeOffset.Now) is not { } next)
        {
            return;
        }

        var wait = next.At - DateTimeOffset.Now;
        _prayerTimer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : wait;
        _prayerTimer.Start();
    }

    private void AnnouncePrayer()
    {
        var now = DateTimeOffset.Now;
        if (_place is { } place && PrayerTimes.For(DateOnly.FromDateTime(now.DateTime), place.Latitude, place.Longitude, now.Offset)
                .FirstOrDefault(prayer => Math.Abs((prayer.At - now).TotalSeconds) < 60) is { } current &&
            !Quiet && !_fullscreenHidden)
        {
            ShowTextHud(CrescentGlyph, string.Format(Tr.Text("Waktunya {0} · {1:HH:mm}", "Time for {0} · {1:HH:mm}"), current.Name(Tr.Text), current.At), red: false);
        }

        SchedulePrayer();
    }

    private string PrayerLine(DateTimeOffset now) =>
        NextPrayer(now) is { } next
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} {1:HH:mm}", next.Name(Tr.Text), next.At)
            : string.Empty;

    internal async void PreviewPrivacy(string mode)
    {
        await Task.Delay(1500);
        _privacyPreview = true;
        var state = mode == "camera" ? new PrivacyState(["Zoom"], ["Zoom"]) : new PrivacyState(["Zoom"], []);
        ShowPrivacy(PrivacyState.None, announce: false);
        ShowPrivacy(state, announce: true);
        _hudTimer.Stop();
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1200);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-privacy-{mode}.png"));
        }
    }
}
