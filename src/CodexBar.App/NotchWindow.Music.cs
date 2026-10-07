using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexBar.App.Media;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CodexBar.App;

// Now playing: whatever app plays through Windows' media controls shows up in the notch like on
// a hardware notch — album art and an equalizer while compact, a player when opened, a short toast
// when the track changes. Music and the agent are two pages of the open notch; scrolling switches.
public partial class NotchWindow
{
    private const double MusicWidth = 436;
    private const double MusicMiniWidth = 340;
    private static readonly TimeSpan PausedLinger = TimeSpan.FromMinutes(5);
    private static readonly Brush PageDotOn = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush PageDotOff = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private readonly MediaMonitor _media;
    private readonly DispatcherTimer _musicTick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _eqTick = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Random _eqRandom = new();
    private string _trackKey = string.Empty;
    private DateTimeOffset? _pausedSince;
    private bool _musicMini;
    private NotchView _lastPage = NotchView.Music;
    private IReadOnlyList<LyricLine> _lyrics = [];
    private string _lyricsKey = string.Empty;
    private float? _volume;
    private FrameworkElement? _barDrag;
    private DateTime _lastFlip;
    private int _wheel;

    private bool MusicEnabled => _host.MusicInNotch;

    /// <summary>Something plays (or was paused only a little while ago) and music is on.</summary>
    private bool MusicShowing =>
        MusicEnabled && _media.Current is { } track &&
        (track.IsPlaying || _pausedSince is null || DateTimeOffset.Now - _pausedSince < PausedLinger);

    // The agent page has something to show (a turn running or finished earlier).
    private bool AgentPageAvailable => _state != NotchState.Idle || _activity.Steps.Count > 0;

    private void InitMusic()
    {
        _media.Changed += Media_Changed;
        _musicTick.Tick += (_, _) => RenderMusicProgress();
        _eqTick.Tick += (_, _) => AnimateEq();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_view == NotchView.Music && _musicMini && !Shell.IsMouseOver)
            {
                Collapse();
            }
        };
        // A paused track leaves the compact notch after a while, without any media event.
        var linger = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        linger.Tick += (_, _) =>
        {
            if (_pausedSince is not null && _view == NotchView.Compact)
            {
                ApplyMusicCompact();
                ApplyState();
                ApplyView(animate: true);
                UpdateVisibility();
            }
        };
        linger.Start();
        Closed += (_, _) =>
        {
            linger.Stop();
            _musicTick.Stop();
            _eqTick.Stop();
            _media.Dispose();
        };
        _ = _media.StartAsync();
    }

    /// <summary>Settings changed (music on/off, lyrics): re-read everything.</summary>
    private void RefreshMusic()
    {
        if (!MusicShowing && _view == NotchView.Music)
        {
            Collapse();
        }

        _lyricsKey = string.Empty;
        RenderMusic();
        ApplyMusicCompact();
    }

    private void Media_Changed()
    {
        var track = _media.Current;
        _pausedSince = track is null || track.IsPlaying ? null : _pausedSince ?? DateTimeOffset.Now;
        var key = track?.TrackKey ?? string.Empty;
        var changed = key != _trackKey;
        var first = _trackKey.Length == 0;
        _trackKey = key;

        if (track is null || !MusicShowing)
        {
            if (_view == NotchView.Music)
            {
                Collapse();
            }
        }
        else if (changed)
        {
            _volume = AppVolume.Get(track.AppId);
            if (!first)
            {
                ShowTrackToast();
            }
        }

        RenderMusic();
        var wasCompact = _view == NotchView.Compact;
        ApplyMusicCompact();
        if (wasCompact && _view == NotchView.Compact)
        {
            ApplyView(animate: true);   // the compact notch widens or narrows with the music
        }
        UpdateVisibility();
    }

    // ---- Compact ---------------------------------------------------------------------------

    private void ApplyMusicCompact()
    {
        var music = MusicShowing;
        var track = _media.Current;
        CompactArt.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactArt.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactArt.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        CompactArt.Background = music && track?.Art is { } art
            ? new ImageBrush(art) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("NotchFill");
        // The art stands for "something is live" in place of the logo.
        CompactGlyph.Visibility = music || _state == NotchState.Idle ? Visibility.Collapsed : Visibility.Visible;

        var eq = music && _state == NotchState.Idle;
        CompactEq.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;
        ApplyQuotaRing();
        if (eq)
        {
            QuotaRing.Visibility = Visibility.Collapsed;
        }

        PlaceCompactChatButton();
        UpdateEqTimer();
        RenderPageDots();
    }

    private void UpdateEqTimer()
    {
        var playing = MusicShowing && _media.Current?.IsPlaying == true && IsVisible && !_suspendedForChat;
        var shown = (_view == NotchView.Compact && CompactEq.Visibility == Visibility.Visible) || _view == NotchView.Music;
        if (playing && shown)
        {
            _eqTick.Start();
        }
        else
        {
            _eqTick.Stop();
            SetEq(CompactEq, playing ? null : 0.3);
            SetEq(MusicEq, playing ? null : 0.3);
        }
    }

    private void AnimateEq()
    {
        SetEq(_view == NotchView.Compact ? CompactEq : MusicEq, null);
    }

    private void SetEq(Panel bars, double? level)
    {
        foreach (var bar in bars.Children.OfType<Rectangle>())
        {
            if (bar.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleY = level ?? 0.25 + _eqRandom.NextDouble() * 0.75;
            }
        }
    }

    // ---- Opening, toast and paging ---------------------------------------------------------

    private void OpenMusic(bool mini = false)
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _toastTimer.Stop();
        _musicMini = mini;
        _lastPage = NotchView.Music;
        RenderMusic();
        _view = NotchView.Music;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        _musicTick.Start();
        if (mini)
        {
            _toastTimer.Start();
        }
    }

    private void ShowTrackToast()
    {
        if (!_host.MusicTrackToast || _view != NotchView.Compact || HasPendingRequest || Shell.IsMouseOver ||
            _suspendedForChat || _pressed)
        {
            return;
        }

        OpenMusic(mini: true);
    }

    // Hovering the compact notch: the page seen last, music when the agent has nothing going on.
    private void OpenFromHover()
    {
        if (MusicShowing && (_state == NotchState.Idle || _lastPage == NotchView.Music))
        {
            OpenMusic();
        }
        else if (_state != NotchState.Idle)
        {
            SelectMostRelevant();
            OpenActivity();
        }
    }

    private void Shell_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_view is not (NotchView.Music or NotchView.Activity) || !MusicShowing || !AgentPageAvailable)
        {
            return;
        }

        // Inside the agent's output, scrolling scrolls it until it reaches its end.
        for (var element = e.OriginalSource as DependencyObject; element is not null and not NotchWindow;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ScrollViewer viewer && viewer.ScrollableHeight > 0 &&
                (e.Delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.5 : viewer.VerticalOffset > 0.5))
            {
                return;
            }
        }

        e.Handled = true;
        // Touchpads send many small steps: switch once per notch-worth, and not again right away.
        _wheel += e.Delta;
        if (Math.Abs(_wheel) < 100 || (DateTime.UtcNow - _lastFlip).TotalMilliseconds < 350)
        {
            return;
        }

        var down = _wheel < 0;
        _wheel = 0;
        var target = down ? NotchView.Activity : NotchView.Music;
        if (target == _view)
        {
            return;
        }

        _lastFlip = DateTime.UtcNow;
        if (target == NotchView.Music)
        {
            OpenMusic();
        }
        else
        {
            _lastPage = NotchView.Activity;
            _musicTick.Stop();
            OpenActivity();
            RenderPageDots();
            UpdateEqTimer();
        }
    }

    private void RenderPageDots()
    {
        var paged = (_view == NotchView.Music && !_musicMini) || _view == NotchView.Activity;
        PageDots.Visibility = paged && MusicShowing && AgentPageAvailable ? Visibility.Visible : Visibility.Collapsed;
        PageDotMusic.Fill = _view == NotchView.Music ? PageDotOn : PageDotOff;
        PageDotAgent.Fill = _view == NotchView.Activity ? PageDotOn : PageDotOff;
    }

    internal async void PreviewMusic(string mode)
    {
        for (var i = 0; i < 40 && !MusicShowing; i++)
        {
            await Task.Delay(100);   // the media session arrives asynchronously
        }

        if (MusicShowing)
        {
            if (mode != "compact")
            {
                OpenMusic(mini: mode == "toast");
                _toastTimer.Stop();
                _pinned = true;   // stays open for the screenshot
            }
            if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
            {
                await Task.Delay(1500);   // let the spring settle
                SnapshotShell(System.IO.Path.Combine(dir, $"notch-music-{mode}.png"));
            }
        }
    }

    // Renders only the notch's own shape to a PNG (nothing else on screen).
    private void SnapshotShell(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(Shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Shell.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(Shell), null, new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    // ---- Player ----------------------------------------------------------------------------

    private void RenderMusic()
    {
        var track = _media.Current;
        if (track is null)
        {
            return;
        }

        MusicTitle.Text = track.Title.Length > 0 ? track.Title : track.Album;
        MusicArtist.Text = track.Artist;
        MusicSource.Text = string.Format(Tr.Text("dari {0}", "from {0}"), track.AppName);
        if (track.Art is { } art)
        {
            MusicArt.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            MusicArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            MusicArt.Background = (Brush)FindResource("NotchFill");
            MusicArtPlaceholder.Visibility = Visibility.Visible;
        }

        MusicPlayGlyph.Visibility = track.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        MusicPauseGlyph.Visibility = track.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(MusicPlayButton,
            track.IsPlaying ? Tr.Text("Jeda", "Pause") : Tr.Text("Putar", "Play"));
        MusicPrevButton.IsEnabled = track.CanPrevious;
        MusicNextButton.IsEnabled = track.CanNext;
        MusicPrevButton.Opacity = track.CanPrevious ? 1 : 0.35;
        MusicNextButton.Opacity = track.CanNext ? 1 : 0.35;
        MusicProgress.Cursor = track.CanSeek ? Cursors.Hand : null;

        MusicFull.Visibility = _musicMini ? Visibility.Collapsed : Visibility.Visible;
        MusicPanel.Width = (_musicMini ? MusicMiniWidth : MusicWidth) - MusicPanel.Margin.Left - MusicPanel.Margin.Right;
        MusicVolumeRow.Visibility = _volume is null ? Visibility.Collapsed : Visibility.Visible;
        MusicVolumeLabel.Text = string.Format(Tr.Text("Volume {0}", "{0} volume"), track.AppName);
        RenderVolume();
        LoadLyrics(track);
        RenderMusicProgress();
        RenderPageDots();
    }

    private void RenderMusicProgress()
    {
        var track = _media.Current;
        if (track is null || _view != NotchView.Music)
        {
            return;
        }

        var position = track.EstimatedPosition;
        var known = track.Duration > TimeSpan.Zero;
        var fraction = known ? Math.Clamp(position / track.Duration, 0, 1) : 0;
        if (_barDrag != MusicProgress)
        {
            MusicProgressFill.Width = Math.Max(0, MusicProgress.ActualWidth * fraction);
        }

        MusicElapsed.Text = FormatTime(position);
        RenderVolume();   // the bars only have a width once laid out
        MusicRemaining.Text = known ? "-" + FormatTime(track.Duration - position) : string.Empty;

        var showLyrics = _lyrics.Count > 0 && _lyricsKey == track.TrackKey;
        MusicLyrics.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
        if (showLyrics)
        {
            var index = SyncedLyrics.IndexAt(_lyrics, position);
            MusicLyricNow.Text = index >= 0 && _lyrics[index].Text.Length > 0 ? _lyrics[index].Text : "♪";
            MusicLyricNext.Text = index + 1 < _lyrics.Count ? _lyrics[index + 1].Text : string.Empty;
        }
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private async void LoadLyrics(MediaSnapshot track)
    {
        if (!_host.MusicLyrics || _lyricsKey == track.TrackKey)
        {
            if (!_host.MusicLyrics)
            {
                _lyrics = [];
            }
            return;
        }

        _lyricsKey = track.TrackKey;
        _lyrics = [];
        var lines = await LyricsClient.GetAsync(track.Title, track.Artist, track.Duration);
        if (_lyricsKey == track.TrackKey)
        {
            _lyrics = lines;
            RenderMusicProgress();
        }
    }

    private void RenderVolume()
    {
        if (_volume is { } volume && _barDrag != MusicVolume)
        {
            MusicVolumeFill.Width = Math.Max(0, MusicVolume.ActualWidth * volume);
        }
    }

    private void MusicPlay_Click(object sender, RoutedEventArgs e) => _media.TogglePlayPause();

    private void MusicPrev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    private void MusicNext_Click(object sender, RoutedEventArgs e) => _media.Next();

    // The progress and volume bars: click or drag along them.
    private void MusicBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var bar = (FrameworkElement)sender;
        if (bar == MusicProgress && _media.Current?.CanSeek != true)
        {
            return;
        }

        e.Handled = true;   // not the start of dragging the notch around
        _barDrag = bar;
        bar.CaptureMouse();
        DragBar(bar, e.GetPosition(bar).X, commit: bar == MusicVolume);
    }

    private void MusicBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_barDrag == sender)
        {
            DragBar(_barDrag, e.GetPosition(_barDrag).X, commit: _barDrag == MusicVolume);
        }
    }

    private void MusicBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_barDrag != sender)
        {
            return;
        }

        e.Handled = true;
        var bar = _barDrag;
        DragBar(bar, e.GetPosition(bar).X, commit: true);
        _barDrag = null;
        bar.ReleaseMouseCapture();
    }

    private void DragBar(FrameworkElement bar, double x, bool commit)
    {
        var width = bar.ActualWidth;
        if (width <= 0 || _media.Current is not { } track)
        {
            return;
        }

        var fraction = Math.Clamp(x / width, 0, 1);
        if (bar == MusicVolume)
        {
            MusicVolumeFill.Width = width * fraction;
            if (commit)
            {
                _volume = (float)fraction;
                AppVolume.Set(track.AppId, (float)fraction);
            }
        }
        else
        {
            MusicProgressFill.Width = width * fraction;
            MusicElapsed.Text = FormatTime(track.Duration * fraction);
            if (commit && track.Duration > TimeSpan.Zero)
            {
                _media.Seek(track.Duration * fraction);
            }
        }
    }
}
