using System.Net.Http;
using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.App.Media;

/// <summary>
/// Synced lyrics from LRCLIB (free, no account): asked once per track by title, artist and
/// length, kept for the session. Only used when the person turns lyrics on (it sends the track's
/// title and artist to lrclib.net).
/// </summary>
internal static class LyricsClient
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, IReadOnlyList<LyricLine>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBar (Windows notch; lyrics in the notch)");
        return client;
    }

    /// <summary>The synced lines, or an empty list when LRCLIB has none.</summary>
    public static async Task<IReadOnlyList<LyricLine>> GetAsync(string title, string artist, TimeSpan duration)
    {
        var key = $"{artist}|{title}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        IReadOnlyList<LyricLine> lines = [];
        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (duration > TimeSpan.Zero)
            {
                url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
            }

            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
                {
                    lines = SyncedLyrics.Parse(synced.GetString());
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Lyrics", exception.Message);
            return lines; // not cached: try again next time
        }

        lock (Cache)
        {
            Cache[key] = lines;
        }

        return lines;
    }
}
