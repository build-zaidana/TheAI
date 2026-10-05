using System.Text.RegularExpressions;

namespace CodexBar.App;

/// <summary>
/// "Read aloud" for an answer, with the Windows voice (SAPI): starting another stops the one
/// playing, and the same answer again stops it. Markdown marks and code blocks are left out.
/// </summary>
internal static class ReadAloud
{
    private const int Async = 1, Purge = 2;
    private static dynamic? _voice;
    private static string? _current;

    /// <summary>The message being read (its key), or null.</summary>
    public static string? Current => _current is not null && IsSpeaking() ? _current : null;

    public static void Toggle(string key, string markdown, string language)
    {
        try
        {
            _voice ??= CreateVoice(language);
            if (_voice is null)
            {
                return;
            }

            if (Current == key)
            {
                _voice.Speak(string.Empty, Async | Purge);
                _current = null;
                return;
            }

            _voice.Speak(Plain(markdown), Async | Purge);
            _current = key;
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("ReadAloud", exception.Message);
        }
    }

    public static void Stop()
    {
        try
        {
            _voice?.Speak(string.Empty, Async | Purge);
        }
        catch (Exception)
        {
        }

        _current = null;
    }

    private static bool IsSpeaking()
    {
        try
        {
            return _voice is not null && (int)_voice.Status.RunningState == 2; // SRSEIsSpeaking
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The default voice, or one for the app's language when Windows has it (LCID 421 = Indonesian).
    private static dynamic? CreateVoice(string language)
    {
        if (Type.GetTypeFromProgID("SAPI.SpVoice") is not { } type)
        {
            return null;
        }

        dynamic voice = Activator.CreateInstance(type)!;
        var wanted = language == "id" ? "421" : "409";
        try
        {
            var voices = voice.GetVoices();
            for (var i = 0; i < (int)voices.Count; i++)
            {
                var candidate = voices.Item(i);
                if (((string)candidate.GetAttribute("Language")).Split(';').Contains(wanted, StringComparer.OrdinalIgnoreCase))
                {
                    voice.Voice = candidate;
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Keep the default voice.
        }

        return voice;
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
