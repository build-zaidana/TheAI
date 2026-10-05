using CodexBar.Core;

namespace CodexBar.App;

// What happens when an answer is ready. If the user is probably looking (the bar window is
// active, or the notch is showing and the turn was short) the sound alone is enough; otherwise
// a Windows notification says which conversation finished, previews the answer and offers
// "Lihat" and "Salin jawaban". The notification carries the sound itself, so it never plays twice.
public partial class MainWindow
{
    private static readonly TimeSpan LongTurn = TimeSpan.FromSeconds(30);

    private readonly CompletionNotifier _notifier = new();
    private readonly Dictionary<string, string> _notifiedAnswers = new(StringComparer.Ordinal);
    private DateTimeOffset _turnStartedAt = DateTimeOffset.Now;

    private void NotifyCompletion(string activityKey, string provider, string workspace, string answer, TimeSpan duration)
    {
        _notifiedAnswers[activityKey] = answer;
        var looking = _notch?.IsVisible == true && duration < LongTurn;

        if (!looking && _settings.CompletionNotification)
        {
            var title = activityKey == NotchWindow.MainActivityKey
                ? LF("notify.doneTitle", provider, workspace)
                : LF("notify.agentDoneTitle", provider, workspace);
            var preview = CompletionNotifier.Preview(answer);
            if (_notifier.Show(title, preview.Length > 0 ? preview : L("notification.responseDone"), activityKey, _settings.CompletionSound))
            {
                return;
            }
        }

        if (_settings.CompletionSound)
        {
            CompletionNotifier.PlaySound();
        }
    }

    private void NotificationActivated(string action, string activityKey)
    {
        if (action == "copy")
        {
            if (_notifiedAnswers.TryGetValue(activityKey, out var answer) && answer.Length > 0)
            {
                try
                {
                    System.Windows.Clipboard.SetText(answer);
                    SetStatus(L("notify.copied"), StatusReadyBrush);
                }
                catch (Exception exception)
                {
                    DiagnosticsLog.Error("Notifier", exception.Message);
                }
            }

            return;
        }

        if (_notch is not null)
        {
            _notch.ShowActivityFor(activityKey);
        }
        else
        {
            ShowFromExternalActivation();
            ShowOutputSurface();
        }
    }
}
