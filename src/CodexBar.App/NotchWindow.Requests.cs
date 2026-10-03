using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CodexBar.Providers;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

namespace CodexBar.App;

// Requests from the agent answered inside the notch instead of a separate dialog: command /
// file / permission approvals, and structured questions (request_user_input). The notch grows
// into a Request view with an orange dot; it does not take keyboard focus from the app the user
// is in. Whatever the user picks completes a TaskCompletionSource the host awaits; requests that
// expire or get answered elsewhere close themselves.
public partial class NotchWindow
{
    private TaskCompletionSource<ProviderApprovalDecision>? _approvalSource;
    private TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyList<string>>?>? _inputSource;
    private ProviderUserInputEvent? _inputRequest;
    private Func<bool>? _requestPending;
    private Uri? _requestUrl;
    private DateTimeOffset? _requestDeadline;
    private readonly Dictionary<string, Func<IReadOnlyList<string>>> _answerReaders = new(StringComparer.Ordinal);
    private DispatcherTimer? _requestTimer;
    private NotchView _viewBeforeRequest = NotchView.Compact;

    /// <summary>Ask the user to approve an action. Esc or Tolak declines.</summary>
    internal Task<ProviderApprovalDecision> RequestApprovalAsync(ProviderApprovalEvent approval, string key = MainActivityKey)
    {
        CancelPendingRequest();
        SelectActivity(ActivityFor(key));
        _approvalSource = new TaskCompletionSource<ProviderApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requestPending = approval.IsPending;

        // Summary: first line is the question, the rest is the command / files / folder.
        var lines = approval.Summary.Replace("\r\n", "\n").Split('\n');
        var title = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? Tr.Text("Agent meminta izin", "The agent needs permission");
        var detail = string.Join('\n', lines.SkipWhile(l => l.Trim() != title).Skip(1)).Trim();

        RequestKind.Text = approval.Method switch
        {
            "item/fileChange/requestApproval" => Tr.Text("Ubah file", "Edit files"),
            "item/permissions/requestApproval" => Tr.Text("Akses tambahan", "Extra access"),
            _ => Tr.Text("Jalankan perintah", "Run a command")
        };
        RequestTitle.Text = title;
        RequestMessage.Visibility = Visibility.Collapsed;
        RequestDetail.Text = detail;
        RequestDetailBox.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestFields.Children.Clear();
        _answerReaders.Clear();

        RequestPrimary.Content = Tr.Text("Izinkan sekali", "Allow once");
        RequestSecondary.Content = Tr.Text("Izinkan sesi", "Allow for session");
        RequestTertiary.Content = Tr.Text("Tolak", "Decline");
        RequestTertiary.Visibility = Visibility.Visible;
        RequestLink.Content = Tr.Text("Tolak & hentikan", "Decline & stop");
        RequestLink.Visibility = Visibility.Visible;
        _requestUrl = null;
        _requestDeadline = null;

        OpenRequestView();
        return _approvalSource.Task;
    }

    /// <summary>Ask the agent's structured question(s). Null means the user cancelled.</summary>
    internal Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> RequestInputAsync(ProviderUserInputEvent input, string key = MainActivityKey)
    {
        CancelPendingRequest();
        SelectActivity(ActivityFor(key));
        _inputSource = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyList<string>>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputRequest = input;
        _requestPending = input.IsPending;

        RequestKind.Text = Tr.Text("Pertanyaan dari agent", "Question from the agent");
        RequestTitle.Text = input.Title;
        RequestMessage.Text = input.Message;
        RequestMessage.Visibility = string.IsNullOrWhiteSpace(input.Message) ? Visibility.Collapsed : Visibility.Visible;
        RequestDetailBox.Visibility = Visibility.Collapsed;
        RequestFields.Children.Clear();
        _answerReaders.Clear();
        foreach (var question in input.Questions)
        {
            AddQuestion(question);
        }

        RequestPrimary.Content = Tr.Text("Lanjutkan", "Continue");
        RequestSecondary.Content = Tr.Text("Batal", "Cancel");
        RequestTertiary.Visibility = Visibility.Collapsed;
        _requestUrl = Uri.TryCreate(input.ActionUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url : null;
        RequestLink.Content = _requestUrl is null ? string.Empty : Tr.Text($"Buka {_requestUrl.Host} ↗", $"Open {_requestUrl.Host} ↗");
        RequestLink.Visibility = _requestUrl is null ? Visibility.Collapsed : Visibility.Visible;
        _requestDeadline = input.AutoResolutionMs is > 0 and <= int.MaxValue
            ? DateTimeOffset.Now.AddMilliseconds(input.AutoResolutionMs.Value)
            : null;

        OpenRequestView();
        return _inputSource.Task;
    }

    private bool HasPendingRequest => _approvalSource is not null || _inputSource is not null;

    // Re-show a request that is still waiting (after a drag, a click or the hotkey).
    private void ShowPendingRequest()
    {
        if (_view == NotchView.Request)
        {
            return;
        }

        _view = NotchView.Request;
        ApplyView(animate: true);
        UpdateVisibility();
    }

    private void OpenRequestView()
    {
        RequestError.Visibility = Visibility.Collapsed;
        RequestCountdown.Text = string.Empty;
        RequestContext.Text = _activity.IsMain
            ? $"{_host.ProviderName} · {_host.WorkspaceName}"
            : $"{_activity.Provider} · {_activity.Workspace} · {L("notch.agents.parallel")}";
        if (_view != NotchView.Request)
        {
            _viewBeforeRequest = _view == NotchView.Launcher ? NotchView.Activity : _view;
        }

        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _view = NotchView.Request;
        ApplyView(animate: true);
        UpdateVisibility();

        _requestTimer ??= CreateRequestTimer();
        _requestTimer.Start();
        TickRequest();
    }

    private DispatcherTimer CreateRequestTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => TickRequest();
        return timer;
    }

    // Expired / answered elsewhere → close. Auto-resolving questions count down and submit.
    private void TickRequest()
    {
        if (_approvalSource is null && _inputSource is null)
        {
            _requestTimer?.Stop();
            return;
        }

        if (_requestPending?.Invoke() == false)
        {
            if (_approvalSource is not null)
            {
                FinishApproval(ProviderApprovalDecision.Decline);
            }
            else
            {
                FinishInput(null);
            }
            return;
        }

        if (_requestDeadline is { } deadline)
        {
            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
            {
                SubmitInput();
                return;
            }

            RequestCountdown.Text = Tr.Text($"Otomatis dalam {Math.Ceiling(remaining.TotalSeconds)} dtk", $"Auto in {Math.Ceiling(remaining.TotalSeconds)}s");
        }
    }

    private void AddQuestion(ProviderInputQuestion question)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (!string.IsNullOrWhiteSpace(question.Header))
        {
            group.Children.Add(new TextBlock
            {
                Text = question.Header,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("NotchText3"),
                Margin = new Thickness(2, 0, 0, 4)
            });
        }

        if (!string.IsNullOrWhiteSpace(question.Question))
        {
            group.Children.Add(new TextBlock
            {
                Text = question.Question,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 0, 8)
            });
        }

        if (question.Options.Count > 0)
        {
            var toggles = new List<(ToggleButton Toggle, string Value)>();
            foreach (var option in question.Options)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(option.Description))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = option.Description,
                        FontSize = 12,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                var toggle = new ToggleButton { Content = content, Style = (Style)FindResource("NotchOption") };
                System.Windows.Automation.AutomationProperties.SetName(toggle, option.Label);
                toggles.Add((toggle, option.Value ?? option.Label));
                group.Children.Add(toggle);
            }

            if (!question.AllowsMultiple)
            {
                // Radio behaviour; first option preselected like the old dialog.
                foreach (var (toggle, _) in toggles)
                {
                    toggle.Checked += (_, _) =>
                    {
                        foreach (var (other, _) in toggles.Where(t => t.Toggle != toggle))
                        {
                            other.IsChecked = false;
                        }
                    };
                }
                toggles[0].Toggle.IsChecked = true;
            }

            TextBox? other = null;
            if (question.AllowsOther)
            {
                other = AddTextField(group, Tr.Text("Lainnya…", "Other…"), multiline: false);
                other.TextChanged += (_, _) =>
                {
                    if (other.Text.Length > 0 && !question.AllowsMultiple)
                    {
                        foreach (var (toggle, _) in toggles)
                        {
                            toggle.IsChecked = false;
                        }
                    }
                };
            }

            _answerReaders[question.Id] = () =>
            {
                var picked = toggles.Where(t => t.Toggle.IsChecked == true).Select(t => t.Value).ToList();
                if (other is { Text.Length: > 0 })
                {
                    picked.Add(other.Text.Trim());
                }
                return picked;
            };
        }
        else if (question.IsSecret)
        {
            var box = new PasswordBox { Style = (Style)FindResource("NotchSecret"), FontSize = 13 };
            group.Children.Add(FieldFrame(box));
            System.Windows.Automation.AutomationProperties.SetName(box, question.Header);
            _answerReaders[question.Id] = () => [box.Password];
        }
        else
        {
            var box = AddTextField(group, Tr.Text("Ketik jawaban…", "Type an answer…"), multiline: true);
            System.Windows.Automation.AutomationProperties.SetName(box, question.Header);
            _answerReaders[question.Id] = () => [box.Text.Trim()];
        }

        RequestFields.Children.Add(group);
    }

    private TextBox AddTextField(Panel parent, string placeholder, bool multiline)
    {
        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = (System.Windows.Media.Brush)FindResource("NotchText3"),
            IsHitTestVisible = false,
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        var box = new TextBox
        {
            Style = (Style)FindResource("NotchInput"),
            FontSize = 13,
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = multiline ? 40 : 0,
            MaxHeight = 120,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(box);
        parent.Children.Add(FieldFrame(grid));
        return box;
    }

    private static Border FieldFrame(UIElement child) => new()
    {
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 8, 12, 8),
        MinHeight = 36,
        Margin = new Thickness(0, 0, 0, 6),
        Child = child
    };

    // ---- Buttons ---------------------------------------------------------------------------

    private void RequestPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_approvalSource is not null)
        {
            FinishApproval(ProviderApprovalDecision.AcceptOnce);
        }
        else
        {
            SubmitInput();
        }
    }

    private void RequestSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_approvalSource is not null)
        {
            FinishApproval(ProviderApprovalDecision.AcceptForSession);
        }
        else
        {
            FinishInput(null);
        }
    }

    private void RequestTertiary_Click(object sender, RoutedEventArgs e) => FinishApproval(ProviderApprovalDecision.Decline);

    private void RequestLink_Click(object sender, RoutedEventArgs e)
    {
        if (_approvalSource is not null)
        {
            FinishApproval(ProviderApprovalDecision.Cancel);
            return;
        }

        if (_requestUrl is { } url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShowRequestError(exception.Message);
            }
        }
    }

    // Esc in the Request view: decline the approval / cancel the question.
    private bool HandleRequestEscape()
    {
        if (_view != NotchView.Request)
        {
            return false;
        }

        if (_approvalSource is not null)
        {
            FinishApproval(ProviderApprovalDecision.Decline);
        }
        else
        {
            FinishInput(null);
        }
        return true;
    }

    private void SubmitInput()
    {
        if (_inputRequest is not { } request)
        {
            return;
        }

        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (!_answerReaders.TryGetValue(question.Id, out var read))
            {
                continue;
            }

            var values = read().Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            if (question.IsRequired && values.Length == 0)
            {
                ShowRequestError(Tr.Text($"“{question.Header}” wajib diisi.", $"“{question.Header}” is required."));
                return;
            }

            if (values.Length > 0 && !ValuesMatch(question.ValueType, values))
            {
                ShowRequestError(question.ValueType == "integer"
                    ? Tr.Text($"“{question.Header}” harus bilangan bulat.", $"“{question.Header}” must be a whole number.")
                    : Tr.Text($"“{question.Header}” harus angka.", $"“{question.Header}” must be a number."));
                return;
            }

            if (values.Length > 0)
            {
                answers[question.Id] = values;
            }
        }

        FinishInput(answers);
    }

    private static bool ValuesMatch(string valueType, IEnumerable<string> values) => valueType switch
    {
        "integer" => values.All(v => long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)),
        "number" => values.All(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)),
        _ => true
    };

    private void ShowRequestError(string message)
    {
        RequestError.Text = message;
        RequestError.Visibility = Visibility.Visible;
    }

    private void FinishApproval(ProviderApprovalDecision decision)
    {
        var source = _approvalSource;
        _approvalSource = null;
        CloseRequestView();
        source?.TrySetResult(decision);
    }

    private void FinishInput(IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        var source = _inputSource;
        _inputSource = null;
        _inputRequest = null;
        CloseRequestView();
        source?.TrySetResult(answers);
    }

    // A newer request replaces an older one (the old one is declined / cancelled).
    private void CancelPendingRequest()
    {
        _approvalSource?.TrySetResult(ProviderApprovalDecision.Decline);
        _approvalSource = null;
        _inputSource?.TrySetResult(null);
        _inputSource = null;
        _inputRequest = null;
    }

    private void CloseRequestView()
    {
        _requestTimer?.Stop();
        _requestPending = null;
        _requestDeadline = null;
        if (_view != NotchView.Request)
        {
            return;
        }

        if (_activity.State == NotchState.Waiting)
        {
            SetActivityState(_activity, NotchState.Working);
        }

        _view = _viewBeforeRequest == NotchView.Activity || ActivityBusy(_activity) ? NotchView.Activity : NotchView.Compact;
        if (_view == NotchView.Activity)
        {
            SelectActivity(_activity);
        }
        ApplyView(animate: true);
        UpdateVisibility();
    }
}
