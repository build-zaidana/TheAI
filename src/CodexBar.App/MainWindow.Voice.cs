using System.Windows.Threading;
using CodexBar.App.Voice;
using CodexBar.Core;

namespace CodexBar.App;

// Voice input host side: owns the whisper engine (one server process, shared by every notch
// input), the voice hotkey, and shuts the server down after a while without use so the model
// doesn't sit in memory all day.
public partial class MainWindow
{
    private static readonly TimeSpan VoiceIdleShutdown = TimeSpan.FromMinutes(10);

    private readonly WhisperEngine _voiceEngine = new();
    private DispatcherTimer? _voiceIdleTimer;
    private DateTime _voiceLastUsed = DateTime.MinValue;

    (string Model, string Language) INotchHost.VoiceOptions => (_settings.VoiceModel, _settings.VoiceLanguage);

    Task INotchHost.InstallVoiceAsync(string model, IProgress<double> progress, CancellationToken cancellationToken) =>
        _voiceEngine.InstallAsync(model, progress, cancellationToken);

    async Task INotchHost.WarmUpVoiceAsync()
    {
        TouchVoice();
        try
        {
            await _voiceEngine.WarmUpAsync(_settings.VoiceModel, _settings.VoiceLanguage);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
        }
    }

    async Task<string> INotchHost.TranscribeAsync(byte[] pcm)
    {
        TouchVoice();
        var started = DateTime.UtcNow;
        var text = await _voiceEngine.TranscribeAsync(pcm, _settings.VoiceModel, _settings.VoiceLanguage, CancellationToken.None);
        DiagnosticsLog.Info("Voice",
            $"transcribed {pcm.Length / 32000.0:0.0}s audio in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms, {text.Length} chars");
        return text;
    }

    (bool Ok, string Message) ISettingsHost.ApplyChatHotkey(string gesture)
    {
        try
        {
            _chatHotKey.ChangeGesture(gesture);
            _settings.ChatHotkey = _chatHotKey.CurrentGesture ?? _settings.ChatHotkey;
            _ = SaveSettingsSafeAsync();
            return (true, LF("hotkey.active", _settings.ChatHotkey));
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    (bool Ok, string Message) ISettingsHost.ApplyVoiceHotkey(string gesture)
    {
        try
        {
            _voiceHotKey.ChangeGesture(gesture);
            _settings.VoiceHotkey = _voiceHotKey.CurrentGesture ?? _settings.VoiceHotkey;
            _ = SaveSettingsSafeAsync();
            return (true, LF("hotkey.active", _settings.VoiceHotkey));
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    Task ISettingsHost.InstallVoiceAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _voiceEngine.InstallAsync(_settings.VoiceModel, progress, cancellationToken);

    void ISettingsHost.UninstallVoice()
    {
        try
        {
            _voiceEngine.Uninstall();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
        }
    }

    private void VoiceHotKey_Pressed()
    {
        if (_notch is not null)
        {
            _notch.ToggleVoice();
        }
        else
        {
            SetStatus(L("voice.hotkeyUnavailable"), StatusErrorBrush);
        }
    }

    private void TouchVoice()
    {
        _voiceLastUsed = DateTime.UtcNow;
        if (_voiceIdleTimer is null)
        {
            _voiceIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _voiceIdleTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow - _voiceLastUsed > VoiceIdleShutdown)
                {
                    _voiceEngine.Shutdown();
                    _voiceIdleTimer.Stop();
                }
            };
        }

        _voiceIdleTimer.Start();
    }
}
