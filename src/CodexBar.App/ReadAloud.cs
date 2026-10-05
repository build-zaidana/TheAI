using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace CodexBar.App;

/// <summary>
/// "Read aloud" for an answer with the Windows voices (every installed voice, including the ones
/// added in Settings &gt; Speech): starting another stops the one playing, and the same answer
/// again stops it. A voice for the app's language is used when Windows has one. Markdown marks
/// and code blocks are left out.
/// </summary>
internal static class ReadAloud
{
    private static SpeechSynthesizer? _synthesizer;
    private static MediaPlayer? _player;
    private static string? _current;

    /// <summary>The message being read (its key), or null.</summary>
    public static string? Current => _current;

    public static async void Toggle(string key, string markdown, string language)
    {
        if (_current == key)
        {
            Stop();
            return;
        }

        Stop();
        _current = key;
        try
        {
            _synthesizer ??= new SpeechSynthesizer();
            var prefix = language == "id" ? "id" : "en";
            if (SpeechSynthesizer.AllVoices.FirstOrDefault(voice => voice.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } voice)
            {
                _synthesizer.Voice = voice;
            }

            var stream = await _synthesizer.SynthesizeTextToStreamAsync(Plain(markdown));
            if (_current != key)
            {
                return; // stopped (or another answer started) while the speech was being made
            }

            if (_player is null)
            {
                _player = new MediaPlayer();
                _player.MediaEnded += (_, _) => _current = null;
                _player.MediaFailed += (_, _) => _current = null;
            }

            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception exception)
        {
            _current = null;
            CodexBar.Core.DiagnosticsLog.Error("ReadAloud", exception.Message);
        }
    }

    public static void Stop()
    {
        try
        {
            if (_player is not null)
            {
                _player.Pause();
                _player.Source = null;
            }
        }
        catch (Exception)
        {
        }

        _current = null;
    }

    // Spoken text: no code blocks, no markdown marks, links as their text.
    internal static string Plain(string markdown)
    {
        var text = Regex.Replace(markdown, "```[\\s\\S]*?```", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"^\s{0,3}(#{1,6}|>|[-*+]|\d+[.)])\s+", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*\|?[-:| ]+\|?\s*$", string.Empty, RegexOptions.Multiline);
        text = text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty).Replace("~~", string.Empty).Replace("|", ", ");
        return Regex.Replace(text, @"[ \t]+", " ").Trim();
    }
}
