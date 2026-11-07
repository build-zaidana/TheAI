using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexBar.Core;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexBar.App;

// Select text anywhere, press the quick-capture hotkey: the notch opens on it with what to do —
// translate, explain, summarize, fix the writing, or ask about it. The answer comes back in the
// notch as a parallel agent (the chat stays closed); "Ask…" puts the text into the launcher.
public partial class NotchWindow
{
    private const double SelectionWidth = 460;

    private string _selection = string.Empty;

    private static readonly (string Command, string Id, string En, Key Key)[] SelectionActions =
    [
        ("translate", "Terjemahkan", "Translate", Key.D1),
        ("explain", "Jelaskan", "Explain", Key.D2),
        ("summarize", "Ringkas", "Summarize", Key.D3),
        ("proofread", "Perbaiki tulisan", "Fix writing", Key.D4),
    ];

    /// <summary>The quick-capture hotkey caught a selection: offer what to do with it.</summary>
    public void ShowSelectionActions(string text)
    {
        _selection = text.Trim();
        if (_selection.Length == 0)
        {
            return;
        }

        _collapseTimer.Stop();
        _hoverTimer.Stop();
        SelectionPreview.Text = _selection.Length > 400 ? _selection[..400] + "…" : _selection;
        SelectionButtons.Children.Clear();
        var index = 1;
        foreach (var (command, id, en, _) in SelectionActions)
        {
            var button = new Button
            {
                Style = (Style)FindResource(index == 1 ? "NotchPrimaryPill" : "NotchPill"),
                Height = 30,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 8, 8),
                Content = $"{Tr.Text(id, en)}  {index}",
                Tag = command
            };
            button.Click += async (_, _) => await RunSelectionAsync(command);
            SelectionButtons.Children.Add(button);
            index++;
        }

        var ask = new Button
        {
            Style = (Style)FindResource("NotchPill"),
            Height = 30,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 8, 8),
            Content = Tr.Text("Tanya…", "Ask…")
        };
        ask.Click += (_, _) => AskAboutSelection();
        SelectionButtons.Children.Add(ask);

        _view = NotchView.Selection;
        ApplyView(animate: true);
        UpdateVisibility();
        Activate();
        SelectionPanel.Focus();
    }

    private async Task RunSelectionAsync(string command)
    {
        var smart = SmartCommands.BuiltIn.First(c => c.Name == command);
        var prompt = SmartCommands.Expand(smart, _selection, new SmartCommandContext(string.Empty, string.Empty, Tr.IsEnglish));
        if (prompt is null)
        {
            return;
        }

        await StartParallelAsync(prompt);
    }

    private void AskAboutSelection()
    {
        var quoted = "> " + _selection.Replace("\n", "\n> ") + "\n\n";
        OpenLauncher();
        LauncherInput.Text = quoted;
        LauncherInput.CaretIndex = quoted.Length;
    }

    private async void SelectionPanel_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key is >= Key.NumPad1 and <= Key.NumPad4 ? Key.D1 + (e.Key - Key.NumPad1) : e.Key;
        if (SelectionActions.FirstOrDefault(action => action.Key == key) is { Command: { } command })
        {
            e.Handled = true;
            await RunSelectionAsync(command);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSelectionAsync(SelectionActions[0].Command);
        }
    }

    internal async void PreviewSelection()
    {
        await Task.Delay(400);
        ShowSelectionActions("Kami akan mengirimkan pembaruan jadwal paling lambat hari Jumat, setelah semua tim mengonfirmasi ketersediaan mereka.");
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-selection.png"));
        }
    }
}
