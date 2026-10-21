using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexBar.App.Hud;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Live activities from anything on this PC: a build, a deploy, a download reports itself with
// "CodexBar.App.exe activity build --progress 0.4" (or a JSON line down the user's pipe) and the
// compact notch shows a ring, its name and how far it is; finished, a check and how long it took;
// failed, the one red cross, with a button to hand the failure to the agent.
public partial class NotchWindow
{
    private const double JobsWidth = 420;

    private static readonly Geometry CheckMark = Geometry.Parse("M 4.6,8.2 L 6.8,10.4 L 11.4,5.6");
    private static readonly Geometry CrossMark = Geometry.Parse("M 5.5,5.5 L 10.5,10.5 M 10.5,5.5 L 5.5,10.5");

    private readonly LiveJobBoard _jobs = new();
    private readonly DispatcherTimer _jobTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private LiveJobPipe? _jobPipe;
    private bool _jobSpinning;
    private string _jobFace = string.Empty;   // what the compact notch showed last tick (id/state)

    private bool JobsShowing => _jobs.Headline(DateTimeOffset.Now) is not null;

    private void InitJobs()
    {
        _jobTick.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var changed = _jobs.Prune(now);
            // A terminal command coming into view (after its first seconds) changes the face too.
            var face = _jobs.Headline(now) is { } headline ? headline.Id + "/" + headline.State : string.Empty;
            changed |= face != _jobFace;
            _jobFace = face;
            if (changed || _jobs.Jobs.Count > 0)
            {
                JobsChanged(layout: changed);
            }
        };
        Closed += (_, _) =>
        {
            _jobTick.Stop();
            _jobPipe?.Dispose();
        };
        RestartJobs();
    }

    /// <summary>Settings may have turned the API on or off.</summary>
    private void RestartJobs()
    {
        if (_host.LiveActivities && _jobPipe is null)
        {
            _jobPipe = new LiveJobPipe();
            _jobPipe.Received += update => Dispatcher.BeginInvoke(() => ApplyJob(update));
            _jobPipe.Start();
        }
        else if (!_host.LiveActivities && _jobPipe is not null)
        {
            _jobPipe.Dispose();
            _jobPipe = null;
            foreach (var job in _jobs.Jobs.ToList())
            {
                _jobs.Dismiss(job.Id);
            }

            JobsChanged(layout: true);
        }
    }

    private void ApplyJob(LiveJobUpdate update)
    {
        if (_jobPipe is null && !_jobsPreview)
        {
            return;
        }

        var before = _jobs.Headline(DateTimeOffset.Now);
        var job = _jobs.Apply(update, DateTimeOffset.Now);
        var after = _jobs.Headline(DateTimeOffset.Now);
        // The compact notch changes shape only when its face changes (appears, goes, finishes).
        JobsChanged(layout: before is null != after is null || before?.State != after?.State || job is null);
        if (job is { State: LiveJobState.Done or LiveJobState.Failed } && ReferenceEquals(job, after) && _view == NotchView.Compact)
        {
            PulseCompact();
        }

        if (job is { State: LiveJobState.Done or LiveJobState.Failed } && (update.Done || update.Failed is not null))
        {
            if (job.Id.StartsWith("sh-", StringComparison.Ordinal))
            {
                RecordDay(day => day.Command(job.State == LiveJobState.Done));
            }
            else if (job.Id == "gh-pr")
            {
                RecordDay(day => day.PullRequest(job.Title + (job.State == LiveJobState.Done ? " ✓" : " ✕ " + job.Detail)));
            }
        }

        // A push from the terminal: its PR's checks are about to start.
        if (job is { State: LiveJobState.Done } && job.Id.StartsWith("sh-", StringComparison.Ordinal) &&
            (job.Title.StartsWith("git push", StringComparison.OrdinalIgnoreCase) || job.Title.StartsWith("gh pr create", StringComparison.OrdinalIgnoreCase)))
        {
            PullRequestSoon();
        }
    }

    private void JobsChanged(bool layout)
    {
        if (_jobs.Jobs.Count > 0)
        {
            _jobTick.Start();
        }
        else
        {
            _jobTick.Stop();
        }

        ApplyMusicCompact();
        if (_view == NotchView.Jobs)
        {
            if (_jobs.Visible(DateTimeOffset.Now).Count == 0)
            {
                Collapse();
            }
            else
            {
                RenderJobs();
            }
        }
        else if (layout && _view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }

        if (layout)
        {
            UpdateVisibility();
        }
    }

    // A finished job nudges the compact notch once, like the timer does.
    private void PulseCompact()
    {
        var scale = new ScaleTransform(1, 1);
        CompactJob.RenderTransformOrigin = new System.Windows.Point(0.1, 0.5);
        CompactJob.RenderTransform = scale;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new ElasticEase { Oscillations = 1, Springiness = 6 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    // ---- Compact -------------------------------------------------------------------------

    /// <summary>The job face of the compact notch; returns whether it took the notch.</summary>
    private bool ApplyJobCompact()
    {
        var now = DateTimeOffset.Now;
        var job = _jobs.Headline(now);
        CompactJob.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactJob.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactJob.Visibility = job is not null ? Visibility.Visible : Visibility.Collapsed;
        if (job is null)
        {
            CompactJobText.Visibility = Visibility.Collapsed;
            SpinJob(false);
            return false;
        }

        CompactArt.Visibility = Visibility.Collapsed;
        CompactEq.Visibility = Visibility.Collapsed;
        CompactGlyph.Visibility = Visibility.Collapsed;
        QuotaRing.Visibility = Visibility.Collapsed;

        var running = job.State == LiveJobState.Running;
        CompactJobTrack.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CompactJobArc.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CompactJobDot.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CompactJobMark.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        if (running)
        {
            CompactJobArc.Data = RingGeometry(16, 2.2, job.Progress ?? 0.28);
            SpinJob(job.Progress is null);
        }
        else
        {
            SpinJob(false);
            var failed = job.State == LiveJobState.Failed;
            CompactJobDot.Fill = failed ? (Brush)FindResource("NotchRed") : TimerFocusBrush;
            CompactJobMark.Data = failed ? CrossMark : CheckMark;
            CompactJobMark.Stroke = failed ? Brushes.White : Brushes.Black;
        }

        CompactJobTitle.Text = job.Title;
        CompactJobTitle.Visibility = Vertical ? Visibility.Collapsed : Visibility.Visible;
        CompactJobText.Text = JobValue(job, now);
        CompactJobText.Foreground = job.State == LiveJobState.Running && job.Progress is not null ? TimerFocusBrush : TimerQuietBrush;
        // An agent at work keeps the right side; the job then shows only its ring and name.
        CompactJobText.Visibility = _state == NotchState.Idle && _held.Count == 0 && !Vertical ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    private void SpinJob(bool on)
    {
        if (on == _jobSpinning)
        {
            return;
        }

        _jobSpinning = on;
        CompactJobSpin.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>The number beside a job: its percent, else its time; a failure says why on its page.</summary>
    private static string JobValue(LiveJob job, DateTimeOffset now) => job.State switch
    {
        LiveJobState.Running when job.Progress is { } progress => $"{Math.Round(progress * 100):0}%",
        LiveJobState.Failed => job.Detail.Length is > 0 and <= 10 ? job.Detail : "!",
        LiveJobState.Done when job.Elapsed(now) < TimeSpan.FromSeconds(1) && job.Detail.Length > 0 => job.Detail,
        _ => Elapsed(job.Elapsed(now))
    };

    private static string Elapsed(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // ---- The jobs page -------------------------------------------------------------------

    private void OpenJobs()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Jobs;
        RenderJobs();
        _view = NotchView.Jobs;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderJobs()
    {
        var now = DateTimeOffset.Now;
        var jobs = _jobs.Visible(now).OrderBy(job => job.State == LiveJobState.Failed ? 0 : job.State == LiveJobState.Running ? 1 : 2)
            .ThenByDescending(job => job.Updated).ToList();
        // Same rows, new numbers: update in place so a hovered button doesn't flicker away.
        if (JobRows.Children.Count == jobs.Count && JobRows.Children.OfType<FrameworkElement>().Select(row => row.Tag as string)
                .SequenceEqual(jobs.Select(job => job.Id + "/" + job.State)))
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                FillJobRow((FrameworkElement)JobRows.Children[i], jobs[i], now);
            }

            return;
        }

        JobRows.Children.Clear();
        foreach (var job in jobs)
        {
            var row = BuildJobRow(job);
            FillJobRow(row, job, now);
            JobRows.Children.Add(row);
        }

        RenderPageDots();
    }

    private sealed record JobRowParts(Path Arc, Border Fill, TextBlock Detail, TextBlock Value, Border Bar);

    private FrameworkElement BuildJobRow(LiveJob job)
    {
        var grid = new Grid { Height = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon: progress ring while running, a check or the red cross after.
        var icon = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var arc = new Path { Stroke = TimerFocusBrush, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        if (job.State == LiveJobState.Running)
        {
            icon.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), StrokeThickness = 2.2 });
            icon.Children.Add(arc);
        }
        else
        {
            var failed = job.State == LiveJobState.Failed;
            icon.Children.Add(new System.Windows.Shapes.Ellipse { Fill = failed ? (Brush)FindResource("NotchRed") : TimerFocusBrush });
            icon.Children.Add(new Path
            {
                Data = failed ? CrossMark : CheckMark,
                Stroke = failed ? Brushes.White : Brushes.Black,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
        }

        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = job.Title,
            FontSize = 13,
            Foreground = TimerFocusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = job.Title
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        // Middle: a bar when the job knows how far it is, else what it says it is doing.
        var fill = new Border { Background = TimerFocusBrush, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = fill
        };
        var detail = new TextBlock
        {
            FontSize = 12,
            Foreground = TimerQuietBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bar, 2);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(bar);
        grid.Children.Add(detail);

        var value = new TextBlock
        {
            Style = (Style)FindResource("NotchDigits"),
            FontSize = 12,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(value, 3);
        grid.Children.Add(value);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (job.State == LiveJobState.Failed)
        {
            var ask = new Button
            {
                Style = (Style)FindResource("NotchPill"),
                Height = 26,
                FontSize = 12,
                Content = string.Format(Tr.Text("Tanya {0}", "Ask {0}"), _host.ProviderName),
                ToolTip = Tr.Text("Kirim kegagalan ini ke agent", "Send this failure to the agent")
            };
            ask.Click += async (_, _) =>
            {
                _jobs.Dismiss(job.Id);
                JobsChanged(layout: true);
                var detailText = job.Detail.Length > 0 ? $" ({job.Detail})" : string.Empty;
                await _host.SubmitFromNotchAsync(job.Folder.Length > 0
                    ? string.Format(
                        Tr.Text("`{0}` baru saja gagal{1} di folder `{2}`. Jalankan lagi di sana, cari penyebabnya, dan perbaiki.",
                            "`{0}` just failed{1} in `{2}`. Run it there again, find out why, and fix it."),
                        job.Title, detailText, job.Folder)
                    : string.Format(
                        Tr.Text("`{0}` baru saja gagal{1}. Cari tahu penyebabnya di folder ini dan perbaiki.", "`{0}` just failed{1}. Find out why in this folder and fix it."),
                        job.Title, detailText));
                SelectActivity(MainActivity);
                OpenActivity();
            };
            actions.Children.Add(ask);
        }

        if (job.State != LiveJobState.Running)
        {
            var dismiss = new Button
            {
                Style = (Style)FindResource("NotchIconButton"),
                Width = 26,
                Height = 26,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = Tr.Text("Tutup", "Dismiss"),
                Content = new Path { Data = Geometry.Parse("M 0,0 L 8,8 M 8,0 L 0,8"), Stroke = TimerQuietBrush, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }
            };
            System.Windows.Automation.AutomationProperties.SetName(dismiss, Tr.Text("Tutup", "Dismiss"));
            dismiss.Click += (_, _) =>
            {
                _jobs.Dismiss(job.Id);
                JobsChanged(layout: true);
            };
            actions.Children.Add(dismiss);
        }

        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        FrameworkElement row = grid;
        if (job.Url is { } url)
        {
            var open = new Button { Style = (Style)FindResource("NotchRowButton"), Content = grid, ToolTip = url.ToString(), Padding = new Thickness(0) };
            open.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    DiagnosticsLog.Error("LiveJobs", exception.Message);
                }
            };
            row = open;
        }

        var holder = new Border
        {
            BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, JobRows.Children.Count == 0 ? 0 : 1, 0, 0),
            Child = row,
            Tag = job.Id + "/" + job.State
        };
        holder.SetValue(FrameworkElement.DataContextProperty, new JobRowParts(arc, fill, detail, value, bar));
        return holder;
    }

    private void FillJobRow(FrameworkElement row, LiveJob job, DateTimeOffset now)
    {
        if (row.DataContext is not JobRowParts parts)
        {
            return;
        }

        var bar = job.State == LiveJobState.Running && job.Progress is not null;
        parts.Bar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        parts.Detail.Visibility = bar ? Visibility.Collapsed : Visibility.Visible;
        parts.Detail.Text = job.Detail;
        parts.Detail.Foreground = job.State == LiveJobState.Failed ? (Brush)FindResource("NotchRed") : TimerQuietBrush;
        if (bar)
        {
            var track = parts.Bar.ActualWidth > 0 ? parts.Bar.ActualWidth : 120;
            parts.Fill.Width = Math.Max(5, track * job.Progress!.Value);
        }

        parts.Arc.Data = RingGeometry(16, 2.2, job.Progress ?? 0.28);
        parts.Value.Text = job.State == LiveJobState.Failed ? string.Empty : JobValue(job, now);
        parts.Value.Foreground = bar ? TimerFocusBrush : TimerQuietBrush;
    }

    // ---- Dev preview ---------------------------------------------------------------------

    private bool _jobsPreview;

    internal async void PreviewJobs(string mode)
    {
        _jobsPreview = true;
        await Task.Delay(400);
        var now = DateTimeOffset.Now;
        switch (mode)
        {
            case "done":
                _jobs.Apply(new LiveJobUpdate("build", "npm run build", 0.9), now.AddSeconds(-134));
                ApplyJob(new LiveJobUpdate("build", Done: true));
                break;
            case "failed":
                ApplyJob(new LiveJobUpdate("deploy", "deploy", Failed: "exit 1"));
                break;
            case "spinner":
                ApplyJob(new LiveJobUpdate("deploy", "deploy staging", Detail: "menunggu health check"));
                break;
            default:
                _jobs.Apply(new LiveJobUpdate("deploy", "deploy staging", Detail: Tr.Text("menunggu health check", "waiting for health check")), now.AddSeconds(-48));
                _jobs.Apply(new LiveJobUpdate("model", "model.gguf", 0.72), now.AddSeconds(-20));
                if (mode == "open")
                {
                    _jobs.Apply(new LiveJobUpdate("lint", "eslint", Failed: "12 errors"), now.AddSeconds(-5));
                }

                ApplyJob(new LiveJobUpdate("build", "npm run build", 0.4));
                break;
        }

        if (mode == "open")
        {
            OpenJobs();
            _pinned = true;
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            if (mode == "open")
            {
                RenderJobs();
                await Task.Delay(200);
            }

            SnapshotShell(System.IO.Path.Combine(dir, $"notch-jobs-{mode}.png"));
        }
    }
}
