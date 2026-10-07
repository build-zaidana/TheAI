using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using global::Windows.Media.Control;

namespace CodexBar.App.Media;

/// <summary>What is playing right now, as Windows' media controls report it.</summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    string AppId,
    string AppName,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionAt,
    bool CanSeek,
    bool CanNext,
    bool CanPrevious,
    ImageSource? Art)
{
    /// <summary>Where playback is now (apps report the position only now and then).</summary>
    public TimeSpan EstimatedPosition
    {
        get
        {
            var position = IsPlaying ? Position + (DateTimeOffset.Now - PositionAt) : Position;
            if (Duration > TimeSpan.Zero && position > Duration)
            {
                position = Duration;
            }

            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    public string TrackKey => $"{AppId}|{Title}|{Artist}";
}

/// <summary>
/// Follows the system media session (Spotify, a browser playing YouTube, Media Player…) through
/// Windows' global media transport controls: what plays, its artwork and position, and the
/// play / pause / skip / seek commands. Nothing leaves the PC. Changes arrive on the UI thread.
/// </summary>
internal sealed class MediaMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artKey = string.Empty;
    private ImageSource? _art;
    private int _refreshing;

    public MediaMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>The current track, or null when nothing has a media session.</summary>
    public MediaSnapshot? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever the track, play state or position changes.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Follow(_manager.GetCurrentSession());
            Follow(_manager.GetCurrentSession());
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Media", exception.Message);
        }
    }

    private void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_Changed;
            _session.PlaybackInfoChanged -= Session_Changed;
            _session.TimelinePropertiesChanged -= Session_Changed;
        }

        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += Session_Changed;
            session.PlaybackInfoChanged += Session_Changed;
            session.TimelinePropertiesChanged += Session_Changed;
        }

        _ = RefreshAsync();
    }

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // Bursts of events (a track change fires three): one read at a time is enough.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            await Task.Delay(60);
            var session = _session;
            MediaSnapshot? snapshot = null;
            if (session is not null)
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var title = properties?.Title ?? string.Empty;
                var artist = properties?.Artist ?? string.Empty;
                if (title.Length > 0 || artist.Length > 0)
                {
                    var appId = session.SourceAppUserModelId ?? string.Empty;
                    var artKey = $"{appId}|{title}|{artist}|{properties?.AlbumTitle}";
                    if (artKey != _artKey)
                    {
                        _artKey = artKey;
                        _art = properties?.Thumbnail is { } thumbnail ? await LoadArtAsync(thumbnail) : null;
                    }

                    snapshot = new MediaSnapshot(
                        title,
                        artist,
                        properties?.AlbumTitle ?? string.Empty,
                        appId,
                        AppName(appId),
                        playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        timeline.Position,
                        timeline.EndTime - timeline.StartTime,
                        timeline.LastUpdatedTime == default ? DateTimeOffset.Now : timeline.LastUpdatedTime,
                        playback.Controls.IsPlaybackPositionEnabled,
                        playback.Controls.IsNextEnabled,
                        playback.Controls.IsPreviousEnabled,
                        _art);
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                Current = snapshot;
                Changed?.Invoke();
            });
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Media", exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static async Task<ImageSource?> LoadArtAsync(global::Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var source = stream.AsStreamForRead();
            var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // some apps hand out an empty or broken thumbnail
        }
    }

    // A friendly name for the app behind a media session ("Spotify.exe", "MSEdge", …).
    internal static string AppName(string appId)
    {
        var id = appId.Split('!')[0];
        var lower = id.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("spotify") => "Spotify",
            _ when lower.Contains("chrome") => "Chrome",
            _ when lower.Contains("msedge") => "Edge",
            _ when lower.Contains("brave") => "Brave",
            _ when lower.Contains("firefox") || lower == "308046b0af4a39cb" => "Firefox",
            _ when lower.Contains("opera") => "Opera",
            _ when lower.Contains("zunemusic") || lower.Contains("media.player") => "Media Player",
            _ when lower.Contains("applemusic") || lower.Contains("itunes") => "Apple Music",
            _ when lower.Contains("vlc") => "VLC",
            _ when lower.Contains("youtube") => "YouTube Music",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                Path.GetFileNameWithoutExtension(id.Contains('_') ? id.Split('_')[0].Split('.')[^1] : id))
        };
    }

    public void TogglePlayPause() => _ = _session?.TryTogglePlayPauseAsync();

    public void Next() => _ = _session?.TrySkipNextAsync();

    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void Seek(TimeSpan position) => _ = _session?.TryChangePlaybackPositionAsync(position.Ticks);

    public void Dispose() => Follow(null);
}
