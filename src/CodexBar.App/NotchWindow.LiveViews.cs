using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using CodexBar.App.Media;
using CodexBar.Core;

namespace CodexBar.App;

// Which live object each page shows (V3), and the music page's seek bar behaviour from board 04.
public partial class NotchWindow
{
    private Color _musicC1 = IslandTokens.Music, _musicC2 = IslandTokens.Music;
    private string _musicColoursFor = string.Empty;

    /// <summary>Called with every view change and whenever a page's live state changes.</summary>
    private void ApplyLiveObject()
    {
        switch (_view)
        {
            case NotchView.Music:
                ApplyMusicObject();
                break;
            case NotchView.Timer or NotchView.TimerDone:
                ApplyTimerObject();
                break;
            case NotchView.Activity or NotchView.Request or NotchView.Quick:
                ApplyAgentObject();
                break;
            case NotchView.Compact:
                ApplyCompactObject();
                break;
            default:
                ClearLiveObject();
                break;
        }
    }

    // ---- The compact pill (board 01): the same objects, small, peeking from the pill's left end ----

    private const double PeekRadius = 16, PeekOut = 20, PeekLow = 6;
    private bool _compactObjectQueued;

    /// <summary>The compact pill's content changes from many places; settle the object once after them.</summary>
    private void QueueCompactObject()
    {
        if (_view != NotchView.Compact || _compactObjectQueued)
        {
            return;
        }

        _compactObjectQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _compactObjectQueued = false;
            ApplyCompactObject();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplyCompactObject()
    {
        var hidden = new List<UIElement>();
        var shown = _view == NotchView.Compact && LiveObjectAllowed && !_dragging && !_liquid;
        if (shown && _state is NotchState.Working or NotchState.Waiting or NotchState.Done or NotchState.Error)
        {
            // The agent: a turning ring while it works, a yellow dot when it needs you, a disc when it ends.
            var (colour, key) = _state switch
            {
                NotchState.Working => (IslandTokens.Agent, "working"),
                NotchState.Waiting => (IslandTokens.Wait, "wait"),
                NotchState.Error => (IslandTokens.Error, "error"),
                _ => (IslandTokens.Agent, "done")
            };
            var state = _state;
            SetLiveObject(LiveKind.Orb, PeekRadius, IslandTokens.Hc / 2, PeekOut, ("peek", key),
                r => state == NotchState.Working ? BuildMiniRing(r, 0.3, colour, spin: true)
                    : BuildMiniMark(r, colour, state switch { NotchState.Done => CheckGlyph, NotchState.Error => CrossGlyph, _ => null }),
                low: PeekLow);
            hidden.Add(CompactGlyph);
            hidden.Add(CompactIndicator);
        }
        else if (shown && CompactTimer.Visibility == Visibility.Visible && _timer.IsActive && _timer.IsCountdown && _timer.Run == TimerRun.Running)
        {
            var colour = TimerColour();
            var fraction = _timer.Duration > TimeSpan.Zero ? _timer.Remaining(DateTimeOffset.Now) / _timer.Duration : 0;
            SetLiveObject(LiveKind.Dial, PeekRadius, IslandTokens.Hc / 2, PeekOut, ("peek-dial", Math.Round(fraction, 2)),
                r => BuildMiniRing(r, fraction, colour, spin: false), low: PeekLow);
            hidden.Add(CompactTimerRing);
        }
        else if (shown && CompactArt.Visibility == Visibility.Visible && _media.Current is { IsPlaying: true } track)
        {
            if (_musicColoursFor != track.TrackKey)
            {
                (_musicC1, _musicC2) = StrongColours(track.Art, IslandTokens.Music);
                _musicColoursFor = track.TrackKey;
            }

            Brush label = track.Art is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new LinearGradientBrush(_musicC1, _musicC2, 45);
            SetLiveObject(LiveKind.Record, PeekRadius, IslandTokens.Hc / 2, PeekOut, ("peek", track.TrackKey),
                r => BuildRecord(r, label, _musicC1, spinning: true), low: PeekLow);
            hidden.Add(CompactArt);
        }
        else if (_view == NotchView.Compact)
        {
            ClearLiveObject();
        }

        // What the drop stands in for steps aside (it keeps its place, so nothing else moves).
        foreach (var element in new UIElement[] { CompactGlyph, CompactIndicator, CompactTimerRing, CompactArt })
        {
            element.Opacity = hidden.Contains(element) ? 0 : 1;
        }
    }

    // ---- Timer: the dial ------------------------------------------------------------------------

    /// <summary>Focus and plain timers in the timer's orange, a pomodoro break in green.</summary>
    private Color TimerColour() =>
        _timer.Mode == TimerMode.Pomodoro && _timer.Phase != PomodoroPhase.Focus ? IslandTokens.Agent : IslandTokens.Timer;

    // Open pages carry no live object now (boards 31-34): the round things stay small, on the compact pill.
    private void ApplyTimerObject()
    {
        ClearLiveObject();
        PlaceTimerText(IslandTokens.P);
    }

    /// <summary>The timer's text starts 16 after the dial's edge (or at P 16 with no dial).</summary>
    private void PlaceTimerText(double inset)
    {
        foreach (var panel in new FrameworkElement[] { TimerPanel, TimerDonePanel })
        {
            var margin = new Thickness(inset, IslandTokens.P, IslandTokens.P, IslandTokens.P);
            if (panel.Margin != margin)
            {
                panel.Margin = margin;
                panel.Width = TimerWidth - inset - IslandTokens.P;
            }
        }
    }

    // ---- Agent: the orb --------------------------------------------------------------------------

    private void ApplyAgentObject()
    {
        if (_view != NotchView.Compact)
        {
            ClearLiveObject();
        }

        PlaceAgentText(IslandTokens.P);
    }

    /// <summary>The agent's text starts 16 after the orb's edge (board 05), or at P 16 without it.</summary>
    private void PlaceAgentText(double inset)
    {
        if (!LiveObjectAllowed)
        {
            inset = IslandTokens.P;
        }

        foreach (var (panel, width) in new (FrameworkElement, double)[] { (ActivityPanel, ActivityWidth), (RequestPanel, ActivityWidth), (QuickPanel, QuickWidth) })
        {
            var margin = new Thickness(inset, IslandTokens.P, IslandTokens.P, IslandTokens.P);
            if (panel.Margin != margin)
            {
                panel.Margin = margin;
                panel.Width = width - inset - IslandTokens.P;
            }
        }
    }

    /// <summary>Snapshot runs: the notch area as it is after a moment, to a PNG.</summary>
    internal async void SnapshotLater(string name, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            SnapshotShell(System.IO.Path.Combine(dir, name + ".png"), Root);
        }
    }

    // ---- Quiet chrome: tools show only under the pointer, like the iPhone's island has none at rest ----

    private static void FadeTo(UIElement element, double opacity) =>
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(opacity, TimeSpan.FromMilliseconds(140)));

    private void CompactPanel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => FadeTo(CompactChatButton, 1);

    private void CompactPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => FadeTo(CompactChatButton, 0);

    private void ActivityPanel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => FadeTo(ActivityTools, 1);

    private void ActivityPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => FadeTo(ActivityTools, 0);

    // ---- Music: the record carries the cover's light --------------------------------------------

    private void ApplyMusicObject()
    {
        ClearLiveObject();
        var track = _media.Current;
        MusicWave.Seed = track?.TrackKey ?? string.Empty;
        MusicWave.Playing = _view == NotchView.Music && track is { IsPlaying: true };
    }

    /// <summary>A song to draw the page with when nothing is playing (snapshot runs only; nothing is played).</summary>
    private static MediaSnapshot SampleTrack()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0xFF, 0x7A, 0x45), 0),
                new(Color.FromRgb(0xC2, 0x18, 0x5B), 0.7),
                new(Color.FromRgb(0x4A, 0x14, 0x8C), 1)
            }, new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, 96, 96));
        }

        var art = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        art.Render(visual);
        art.Freeze();
        return new MediaSnapshot("Evaluasi", "Hindia", "Menari dengan Bayangan", "sample", "Spotify", true,
            TimeSpan.FromSeconds(92), TimeSpan.FromSeconds(241), DateTimeOffset.Now, true, true, true, art);
    }
}
