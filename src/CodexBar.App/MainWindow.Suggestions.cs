using System.IO;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Providers.ClaudeCode;

namespace CodexBar.App;

// Next-prompt suggestions. When a turn of the main conversation ends, the notch's inputs show a
// faint guess of what the user will send next (Tab takes it, Enter sends it, typing ignores it).
// The guess comes from the user's own habits (PromptHabits, learnt from short prompts sent right
// after a turn); with "habits + AI" a small Claude model is asked when the habits are not sure.
// The habits are seeded once from existing CodexBar threads and Claude Code sessions.
public partial class MainWindow
{
    private static readonly TimeSpan HabitWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AiSuggestionTimeout = TimeSpan.FromSeconds(40);

    private PromptHabits? _habits;
    private TurnContext? _lastTurnContext;
    private DateTimeOffset _lastTurnEnded;
    private bool _turnChangedFiles;
    private CancellationTokenSource? _suggestionCts;

    private PromptHabits Habits => _habits ??= LoadHabits();

    private PromptHabits LoadHabits()
    {
        var seeded = File.Exists(PromptHabits.DefaultPath);
        var habits = new PromptHabits(PromptHabits.DefaultPath);
        if (!seeded)
        {
            _ = SeedHabitsAsync(habits);
        }

        return habits;
    }

    // First run: learn from conversations that already exist (pairs of answer → next prompt).
    private async Task SeedHabitsAsync(PromptHabits habits)
    {
        try
        {
            var pairs = await Task.Run(() =>
            {
                var conversations = new List<IReadOnlyList<ProviderThreadMessage>>();
                foreach (var provider in new[] { "claude-code", "anthropic", "openai", "openrouter" })
                {
                    foreach (var thread in LocalConversationStore.ListSummaries(provider, 40))
                    {
                        conversations.Add(LocalConversationStore.Load(provider, thread.Id));
                    }
                }

                var found = new List<(string Prompt, TurnContext Context)>();
                foreach (var session in ClaudeSessionFiles.List(30))
                {
                    // Claude Code transcripts also tell which answers edited files.
                    var exchanges = ClaudeSessionFiles.ReadExchanges(session.FilePath);
                    for (var i = 1; i < exchanges.Count; i++)
                    {
                        var previous = exchanges[i - 1];
                        found.Add((exchanges[i].Prompt, PromptHabits.Classify(previous.Answer, previous.ChangedFiles, failed: false)));
                    }
                }

                foreach (var messages in conversations)
                {
                    for (var i = 1; i < messages.Count; i++)
                    {
                        if (messages[i].Role == "user" && messages[i - 1].Role == "assistant")
                        {
                            found.Add((messages[i].Text, PromptHabits.Classify(messages[i - 1].Text, changedFiles: false, failed: false)));
                        }
                    }
                }

                return found;
            });

            foreach (var (prompt, context) in pairs)
            {
                habits.Record(prompt, context);
            }

            DiagnosticsLog.Info("Suggestions", $"seeded habits from {pairs.Count} follow-ups");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Suggestions", exception.Message);
        }
    }

    /// <summary>Called right before a prompt goes out: learn from it, drop the old suggestion.</summary>
    private void OnPromptSent(string prompt)
    {
        _suggestionCts?.Cancel();
        _notch?.SetSuggestion([], false);
        if (_lastTurnContext is { } context && DateTimeOffset.Now - _lastTurnEnded < HabitWindow && prompt.Length > 0)
        {
            Habits.Record(prompt, context);
        }

        _lastTurnContext = null;
        _turnChangedFiles = false;
    }

    /// <summary>Called when a turn of the main conversation ends.</summary>
    private void OnTurnEnded(string prompt, string answer, bool failed)
    {
        var context = PromptHabits.Classify(answer, _turnChangedFiles, failed);
        _lastTurnContext = context;
        _lastTurnEnded = DateTimeOffset.Now;
        if (!_settings.PromptSuggestions)
        {
            return;
        }

        var habits = Habits.PredictMany(context, 3);
        if (habits.Count > 0)
        {
            _notch?.SetSuggestion(habits, false);
            return;
        }

        if (_settings.PromptSuggestionSource == "habit+ai" && !failed && answer.Length > 0)
        {
            _ = SuggestWithAiAsync(prompt, answer);
        }
    }

    private async Task SuggestWithAiAsync(string prompt, string answer)
    {
        _suggestionCts?.Cancel();
        var cts = new CancellationTokenSource(AiSuggestionTimeout);
        _suggestionCts = cts;
        var question = string.Join("\n",
            "You predict the next message a developer will send to their AI coding agent.",
            "Read their last request and the agent's reply, then write the single most likely short follow-up they will send next:",
            "in the same language the developer writes in, at most 8 words, imperative, no quotes, no explanation, nothing else.",
            string.Empty,
            "Developer: " + Truncate(prompt, 600),
            string.Empty,
            "Agent: " + Truncate(answer, 1500, fromEnd: true));
        var started = DateTime.UtcNow;
        var reply = await ClaudeCodeProvider.AskOnceAsync(question, "haiku", cts.Token);
        if (cts.IsCancellationRequested || reply is null)
        {
            return;
        }

        var line = reply.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Trim('"', '\'', '“', '”', '`') ?? string.Empty;
        DiagnosticsLog.Info("Suggestions", $"ai suggestion in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: {line}");
        if (line.Length is > 0 and <= 80 && _lastTurnContext is not null)
        {
            _notch?.SetSuggestion([line], true);
        }
    }

    private static string Truncate(string text, int max, bool fromEnd = false) =>
        text.Length <= max ? text : fromEnd ? "…" + text[^max..] : text[..max] + "…";
}
