using System.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

// The faint next-prompt guess shown in an empty input after the main conversation's turn ends
// (the guess itself comes from the host). It belongs to the main conversation only, so the
// follow-up input of a parallel agent or an outside session never shows it. Typing hides it, Tab
// copies it into the input, Enter or the send button sends it as is.
public partial class NotchWindow
{
    private string? _suggestion;
    private IReadOnlyList<string> _suggestions = [];
    private bool _suggestionFromAi;

    /// <summary>Show the next-prompt guesses, best first (an empty list clears them).</summary>
    public void SetSuggestion(IReadOnlyList<string> guesses, bool fromAi)
    {
        _suggestions = guesses.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();
        _suggestion = _suggestions.FirstOrDefault();
        _suggestionFromAi = fromAi;
        RenderSuggestion();
        ApplyState();
        UpdateLauncherSendState();
    }

    private string? SuggestionFor(TextBox input)
    {
        if (_suggestion is null || input.Text.Length > 0 || _voice != VoiceState.Off)
        {
            return null;
        }

        return ReferenceEquals(input, FollowUpInput) && !(_activity.IsMain && !ActivityBusy(_activity)) ? null : _suggestion;
    }

    // Tab in an empty input takes the best guess; Tab again on an untouched guess moves to the next.
    private bool TakeSuggestion(TextBox input)
    {
        var current = Array.IndexOf(_suggestions.ToArray(), input.Text);
        if (current >= 0 && _suggestions.Count > 1)
        {
            var next = _suggestions[(current + 1) % _suggestions.Count];
            input.Text = next;
            input.CaretIndex = next.Length;
            return true;
        }

        if (SuggestionFor(input) is not { } suggestion)
        {
            return false;
        }

        input.Text = suggestion;
        input.CaretIndex = suggestion.Length;
        return true;
    }

    private void RenderSuggestion()
    {
        var followUp = SuggestionFor(FollowUpInput);
        FollowUpSuggestion.Visibility = followUp is null ? Visibility.Collapsed : Visibility.Visible;
        FollowUpSuggestionText.Text = followUp ?? string.Empty;
        FollowUpSuggestionAi.Visibility = _suggestionFromAi ? Visibility.Visible : Visibility.Collapsed;
        if (followUp is not null)
        {
            FollowUpPlaceholder.Visibility = Visibility.Collapsed;
        }

        var launcher = SuggestionFor(LauncherInput);
        LauncherSuggestion.Visibility = launcher is null ? Visibility.Collapsed : Visibility.Visible;
        LauncherSuggestionText.Text = launcher ?? string.Empty;
        LauncherSuggestionAi.Visibility = _suggestionFromAi ? Visibility.Visible : Visibility.Collapsed;
        if (launcher is not null)
        {
            LauncherPlaceholder.Visibility = Visibility.Collapsed;
        }
        else if (LauncherInput.Text.Length == 0)
        {
            LauncherPlaceholder.Visibility = Visibility.Visible;
        }
    }
}
