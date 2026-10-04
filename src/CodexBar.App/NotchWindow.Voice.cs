using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexBar.App.Voice;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

// Voice input. Hold the mic button to talk and let go to transcribe; a short tap instead starts
// a hands-free take that the next tap (or the voice hotkey, or Enter) ends. Esc throws the take
// away. Audio is captured at 16 kHz and transcribed offline by whisper.cpp; the text lands at the
// caret of the launcher input, or of the follow-up input when the activity view is open. The
// first use asks before downloading the engine and model.
public partial class NotchWindow
{
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);
    private static readonly Brush RecordingBrush = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    private enum VoiceState { Off, Recording, Transcribing }

    private readonly WaveRecorder _recorder = new();
    private VoiceState _voice = VoiceState.Off;
    private DateTime _voicePressedAt;
    private bool _voiceHandsFree;
    private TextBox? _voiceTarget;
    private Grid? _voiceHost;
    private Border[] _voiceBars = [];
    private TextBlock? _voiceLabel;
    private CancellationTokenSource? _voiceDownload;
    private bool _voiceRecordAfterInstall;

    /// <summary>Voice hotkey: start a hands-free take, or finish the running one.</summary>
    public void ToggleVoice()
    {
        if (_voice == VoiceState.Recording)
        {
            _ = FinishVoiceAsync();
            return;
        }

        if (_voice == VoiceState.Transcribing)
        {
            return;
        }

        if (_view is not (NotchView.Launcher or NotchView.Activity))
        {
            OpenLauncher();
        }
        else
        {
            Activate();
        }

        StartVoice(handsFree: true);
    }

    private void MicButton_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_voice == VoiceState.Recording)
        {
            _ = FinishVoiceAsync(); // second tap of a hands-free take
            return;
        }

        _voicePressedAt = DateTime.UtcNow;
        StartVoice(handsFree: false);
    }

    private void MicButton_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_voice != VoiceState.Recording || _voiceHandsFree)
        {
            return;
        }

        if (DateTime.UtcNow - _voicePressedAt < HoldThreshold)
        {
            // A tap: keep listening until the next tap.
            _voiceHandsFree = true;
            SetVoiceLabel(L("voice.listeningTap"));
        }
        else
        {
            _ = FinishVoiceAsync();
        }
    }

    private void StartVoice(bool handsFree)
    {
        var (model, language) = _host.VoiceOptions;
        if (!WhisperEngine.IsInstalled(model))
        {
            ShowVoiceSetup(recordAfter: handsFree);
            return;
        }

        _voiceTarget = _view == NotchView.Activity ? FollowUpInput : LauncherInput;
        _voiceHost = _view == NotchView.Activity ? FollowUpVoiceHost : LauncherVoiceHost;
        try
        {
            _recorder.Level -= Recorder_Level;
            _recorder.Level += Recorder_Level;
            _recorder.Start();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
            ShowVoiceError(exception.Message);
            return;
        }

        _voice = VoiceState.Recording;
        _voiceHandsFree = handsFree;
        _collapseTimer.Stop();
        BuildVoiceOverlay(handsFree ? L("voice.listeningTap") : L("voice.listeningHold"));
        _ = _host.WarmUpVoiceAsync(); // usually already running; costs nothing then
    }

    private async Task FinishVoiceAsync()
    {
        if (_voice != VoiceState.Recording)
        {
            return;
        }

        var pcm = _recorder.Stop();
        _voice = VoiceState.Transcribing;
        SetVoiceLabel(L("voice.transcribing"));
        foreach (var bar in _voiceBars)
        {
            bar.Height = 3;
        }

        try
        {
            var text = await _host.TranscribeAsync(pcm);
            InsertTranscript(text);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
            ShowVoiceError(exception.Message);
        }
        finally
        {
            EndVoiceOverlay();
        }
    }

    /// <summary>Esc while recording: discard the take.</summary>
    private bool CancelVoice()
    {
        if (_voice != VoiceState.Recording)
        {
            return false;
        }

        _recorder.Stop();
        EndVoiceOverlay();
        return true;
    }

    private void InsertTranscript(string text)
    {
        if (_voiceTarget is not { } target || text.Length == 0)
        {
            return;
        }

        var caret = target.CaretIndex;
        var before = target.Text[..caret];
        var after = target.Text[caret..];
        var spacer = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : string.Empty;
        target.Text = before + spacer + text + (after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : string.Empty) + after;
        target.CaretIndex = before.Length + spacer.Length + text.Length;
        target.Focus();
    }

    // ---- Overlay -----------------------------------------------------------------------------

    private void BuildVoiceOverlay(string label)
    {
        if (_voiceHost is null || _voiceTarget is null)
        {
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = RecordingBrush, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        row.Children.Add(dot);

        var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _voiceBars = Enumerable.Range(0, 7).Select(_ => new Border
        {
            Width = 3,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Brushes.White,
            Margin = new Thickness(1.5, 0, 1.5, 0),
            VerticalAlignment = VerticalAlignment.Center
        }).ToArray();
        foreach (var bar in _voiceBars)
        {
            bars.Children.Add(bar);
        }

        row.Children.Add(bars);
        _voiceLabel = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (Brush)FindResource("NotchText2"),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_voiceLabel);

        _voiceHost.Children.Clear();
        _voiceHost.Children.Add(row);
        _voiceHost.Visibility = Visibility.Visible;
        _voiceTarget.Opacity = 0;
        LauncherPlaceholder.Opacity = 0;
        FollowUpPlaceholder.Opacity = 0;
        MicButton.Foreground = RecordingBrush;
    }

    private void SetVoiceLabel(string text)
    {
        if (_voiceLabel is not null)
        {
            _voiceLabel.Text = text;
        }
    }

    private void EndVoiceOverlay()
    {
        _voice = VoiceState.Off;
        _voiceHandsFree = false;
        if (_voiceHost is not null)
        {
            _voiceHost.Children.Clear();
            _voiceHost.Visibility = Visibility.Collapsed;
        }

        if (_voiceTarget is not null)
        {
            _voiceTarget.Opacity = 1;
        }

        LauncherPlaceholder.Opacity = 1;
        FollowUpPlaceholder.Opacity = 1;
        MicButton.ClearValue(ForegroundProperty);
        _voiceBars = [];
        _voiceLabel = null;
    }

    // Capture thread → UI: each bar follows the level with a little per-bar variation so the
    // meter reads as a voice rather than a single bouncing block.
    private void Recorder_Level(double level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            for (var i = 0; i < _voiceBars.Length; i++)
            {
                var shape = 0.55 + 0.45 * Math.Sin((Environment.TickCount64 / 90.0) + i * 1.3);
                _voiceBars[i].Height = 3 + 15 * Math.Clamp(level * shape, 0, 1);
            }
        });
    }

    private void ShowVoiceError(string message)
    {
        if (_view == NotchView.Activity)
        {
            AddStep(_activity, Truncate(message, 120), isError: true);
            return;
        }

        VoiceSetupTitle.Text = L("voice.errorTitle");
        VoiceSetupText.Text = message;
        VoiceProgressTrack.Visibility = Visibility.Collapsed;
        VoiceDownloadButton.Visibility = Visibility.Collapsed;
        VoiceLaterButton.Content = L("voice.dismiss");
        VoiceSetupButtons.Visibility = Visibility.Visible;
        VoiceSetup.Visibility = Visibility.Visible;
    }

    /// <summary>Dev preview only: "setup" shows the download offer, "recording" the listening state (no mic).</summary>
    internal void PreviewVoice(string mode)
    {
        if (mode == "setup")
        {
            ShowVoiceSetup(recordAfter: false);
            return;
        }

        OpenLauncher();
        _voiceTarget = LauncherInput;
        _voiceHost = LauncherVoiceHost;
        BuildVoiceOverlay(L("voice.listeningTap"));
        var heights = new[] { 0.3, 0.7, 1.0, 0.6, 0.85, 0.4, 0.2 };
        for (var i = 0; i < _voiceBars.Length; i++)
        {
            _voiceBars[i].Height = 3 + 15 * heights[i];
        }

        _pinned = true;
    }

    // ---- First use: download -------------------------------------------------------------------

    private void ShowVoiceSetup(bool recordAfter)
    {
        if (_view != NotchView.Launcher)
        {
            OpenLauncher();
        }

        var (model, _) = _host.VoiceOptions;
        _voiceRecordAfterInstall = recordAfter;
        VoiceSetupTitle.Text = L("voice.setupTitle");
        VoiceSetupText.Text = string.Format(L("voice.setupText"), WhisperEngine.DownloadSizeMb(model));
        VoiceDownloadButton.Content = L("voice.download");
        VoiceDownloadButton.Visibility = Visibility.Visible;
        VoiceLaterButton.Content = L("voice.later");
        VoiceSetupButtons.Visibility = Visibility.Visible;
        VoiceProgressTrack.Visibility = Visibility.Collapsed;
        VoiceSetup.Visibility = Visibility.Visible;
    }

    private async void VoiceDownload_Click(object sender, RoutedEventArgs e)
    {
        var (model, _) = _host.VoiceOptions;
        _voiceDownload = new CancellationTokenSource();
        VoiceDownloadButton.Visibility = Visibility.Collapsed;
        VoiceLaterButton.Content = L("voice.cancel");
        VoiceProgressTrack.Visibility = Visibility.Visible;
        VoiceProgressFill.Width = 0;
        var progress = new Progress<double>(value =>
        {
            VoiceProgressFill.Width = Math.Max(0, VoiceProgressTrack.ActualWidth * value);
            VoiceSetupText.Text = string.Format(L("voice.downloading"), Math.Round(value * 100));
        });

        try
        {
            await _host.InstallVoiceAsync(model, progress, _voiceDownload.Token);
            VoiceSetup.Visibility = Visibility.Collapsed;
            _ = _host.WarmUpVoiceAsync();
            if (_voiceRecordAfterInstall)
            {
                StartVoice(handsFree: true);
            }
            else
            {
                LauncherInput.Focus();
            }
        }
        catch (OperationCanceledException)
        {
            VoiceSetup.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
            ShowVoiceError(exception.Message);
        }
        finally
        {
            _voiceDownload?.Dispose();
            _voiceDownload = null;
        }
    }

    private void VoiceLater_Click(object sender, RoutedEventArgs e)
    {
        _voiceDownload?.Cancel();
        VoiceSetup.Visibility = Visibility.Collapsed;
        LauncherInput.Focus();
    }
}
