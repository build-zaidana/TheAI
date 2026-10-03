using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CodexBar.Core;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Files the agent changed during the turn. Edits arrive per step (Codex fileChange items, Claude
// Edit/Write tool calls) and are merged per path; the activity view shows one summary row
// ("3 file diubah +24 −7") that expands into the files, each with a short coloured diff.
public partial class NotchWindow
{
    private const int MaxDiffLines = 40;

    private static readonly Brush AddedLineBrush = Frozen(Color.FromArgb(0x24, 0x30, 0xD1, 0x58));
    private static readonly Brush RemovedLineBrush = Frozen(Color.FromArgb(0x24, 0xFF, 0x45, 0x3A));
    private static readonly Brush AddedTextBrush = Frozen(Color.FromRgb(0x7E, 0xE7, 0x87));
    private static readonly Brush RemovedTextBrush = Frozen(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush ContextTextBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private readonly Dictionary<string, ChangedFile> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _filesExpanded;

    private sealed class ChangedFile(string path)
    {
        public string Path { get; set; } = path;
        public string Kind { get; set; } = "update";
        public List<string> Diffs { get; } = new();
        public int Added { get; set; }
        public int Removed { get; set; }
        public bool Expanded { get; set; }
    }

    /// <summary>Dev preview only: open the activity view with the file list expanded.</summary>
    internal void PreviewActivity()
    {
        _filesExpanded = true;
        if (_changedFiles.Values.FirstOrDefault() is { } first)
        {
            first.Expanded = true;
        }

        RenderChangedFiles();
        OpenActivity();
        _doneTimer.Stop();
        _pinned = true;
        ApplyPin();
    }

    private void ResetChangedFiles()
    {
        _changedFiles.Clear();
        _filesExpanded = false;
        RenderChangedFiles();
    }

    private void AddChangedFiles(IReadOnlyList<ProviderFileChange> changes)
    {
        foreach (var change in changes)
        {
            var target = change.MovePath is { Length: > 0 } moved ? moved : change.Path;
            // A move re-keys the entry under its new path.
            if (!_changedFiles.Remove(change.Path, out var file) && !_changedFiles.TryGetValue(target, out file))
            {
                file = new ChangedFile(target) { Kind = change.Kind };
            }

            file.Path = target;
            _changedFiles[target] = file;

            // An added file stays "added" through later edits; a delete wins over everything.
            if (change.Kind == "delete" || file.Kind != "add")
            {
                file.Kind = change.Kind;
            }

            var diff = NormalizeDiff(change);
            if (diff.Length > 0)
            {
                file.Diffs.Add(diff);
                var (added, removed) = LineDiff.Count(diff);
                file.Added += added;
                file.Removed += removed;
            }
        }

        RenderChangedFiles();
    }

    // Codex sends the whole content (no +/- markers) for added and deleted files.
    private static string NormalizeDiff(ProviderFileChange change)
    {
        var lines = LineDiff.SplitLines(change.Diff);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var marked = lines.Any(line => line.StartsWith('+') || line.StartsWith('-') || line.StartsWith("@@", StringComparison.Ordinal));
        if (marked || change.Kind is not ("add" or "delete"))
        {
            return change.Diff;
        }

        var prefix = change.Kind == "add" ? "+" : "-";
        return string.Concat(lines.Select(line => prefix + line + "\n"));
    }

    private void RenderChangedFiles()
    {
        if (_changedFiles.Count == 0)
        {
            FilesBox.Visibility = Visibility.Collapsed;
            FilesList.Children.Clear();
            return;
        }

        FilesBox.Visibility = Visibility.Visible;
        FilesSummary.Text = string.Format(L("notch.files.summary"), _changedFiles.Count);
        var added = _changedFiles.Values.Sum(file => file.Added);
        var removed = _changedFiles.Values.Sum(file => file.Removed);
        FilesAdded.Text = added > 0 ? $"+{added}" : string.Empty;
        FilesRemoved.Text = removed > 0 ? $"−{removed}" : string.Empty;
        OpenEditorButton.Content = L("notch.files.openEditor");
        FilesScroller.Visibility = _filesExpanded ? Visibility.Visible : Visibility.Collapsed;
        FilesChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        FilesChevronRotation.Angle = _filesExpanded ? 90 : 0;

        FilesList.Children.Clear();
        if (!_filesExpanded)
        {
            return;
        }

        foreach (var file in _changedFiles.Values)
        {
            FilesList.Children.Add(BuildFileRow(file));
            if (file.Expanded)
            {
                FilesList.Children.Add(BuildDiffView(file));
            }
        }
    }

    private FrameworkElement BuildFileRow(ChangedFile file)
    {
        var (badge, badgeBrush) = file.Kind switch
        {
            "add" => ("A", (Brush)FindResource("NotchGreen")),
            "delete" => ("D", (Brush)FindResource("NotchRed")),
            _ => ("M", (Brush)FindResource("NotchAccent"))
        };

        var (name, folder) = SplitDisplayPath(file.Path);
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.Inlines.Add(new System.Windows.Documents.Run(name) { FontSize = 13 });
        if (folder.Length > 0)
        {
            label.Inlines.Add(new System.Windows.Documents.Run("  " + folder) { FontSize = 11, Foreground = (Brush)FindResource("NotchText3") });
        }

        var counts = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (file.Added > 0)
        {
            counts.Inlines.Add(new System.Windows.Documents.Run($"+{file.Added}") { Foreground = (Brush)FindResource("NotchGreen") });
        }
        if (file.Removed > 0)
        {
            counts.Inlines.Add(new System.Windows.Documents.Run($" −{file.Removed}") { Foreground = (Brush)FindResource("NotchRed") });
        }

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badgeText = new TextBlock
        {
            Text = badge,
            Width = 14,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = badgeBrush,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(badgeText);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        Grid.SetColumn(counts, 2);
        content.Children.Add(counts);

        var row = new Button
        {
            Style = (Style)FindResource("NotchRowButton"),
            Height = 32,
            Content = content,
            Tag = file,
            ToolTip = file.Path
        };
        row.Click += FileRow_Click;

        var open = new Button
        {
            Style = (Style)FindResource("NotchIconButton"),
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 4, 0),
            Tag = file,
            ToolTip = L("notch.files.openFile"),
            IsEnabled = file.Kind != "delete",
            Content = new Path
            {
                Stroke = (Brush)FindResource("NotchText2"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 7,1 L 11,1 L 11,5 M 11,1 L 6,6 M 4.5,2 L 1,2 L 1,11 L 10,11 L 10,7.5")
            }
        };
        open.Click += OpenFile_Click;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(row);
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        return grid;
    }

    private FrameworkElement BuildDiffView(ChangedFile file)
    {
        var lines = file.Diffs
            .SelectMany(LineDiff.SplitLines)
            .Where(line => !line.StartsWith("diff ", StringComparison.Ordinal) &&
                           !line.StartsWith("index ", StringComparison.Ordinal) &&
                           !line.StartsWith("+++", StringComparison.Ordinal) &&
                           !line.StartsWith("---", StringComparison.Ordinal) &&
                           !line.StartsWith('\\'))
            .ToList();
        // A leading hunk marker says nothing; later ones mark skipped lines.
        while (lines.Count > 0 && lines[0].StartsWith("@@", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        var panel = new StackPanel { Margin = new Thickness(36, 0, 12, 8) };
        if (lines.Count == 0)
        {
            panel.Children.Add(DiffLine(L("notch.files.noDiff"), ContextTextBrush, null));
        }

        foreach (var line in lines.Take(MaxDiffLines))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                panel.Children.Add(DiffLine("⋯", ContextTextBrush, null));
            }
            else if (line.StartsWith('+'))
            {
                panel.Children.Add(DiffLine(line, AddedTextBrush, AddedLineBrush));
            }
            else if (line.StartsWith('-'))
            {
                panel.Children.Add(DiffLine(line, RemovedTextBrush, RemovedLineBrush));
            }
            else
            {
                panel.Children.Add(DiffLine(line.Length > 0 ? " " + line[1..] : line, ContextTextBrush, null));
            }
        }

        if (lines.Count > MaxDiffLines)
        {
            panel.Children.Add(DiffLine(string.Format(L("notch.files.moreLines"), lines.Count - MaxDiffLines), ContextTextBrush, null));
        }

        return new Border { Child = panel };
    }

    private static Border DiffLine(string text, Brush foreground, Brush? background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 0, 6, 0),
        Child = new TextBlock
        {
            Text = text.Replace("\t", "    "),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            LineHeight = 17,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis
        }
    };

    private (string Name, string Folder) SplitDisplayPath(string path)
    {
        var display = path;
        var workspace = _host.WorkspacePath;
        if (workspace.Length > 0 && System.IO.Path.IsPathRooted(path))
        {
            var relative = System.IO.Path.GetRelativePath(workspace, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            {
                display = relative;
            }
        }

        var name = System.IO.Path.GetFileName(display);
        var folder = System.IO.Path.GetDirectoryName(display) ?? string.Empty;
        return (name.Length > 0 ? name : display, folder.Replace('\\', '/'));
    }

    private string ResolvePath(string path) =>
        System.IO.Path.IsPathRooted(path) || _host.WorkspacePath.Length == 0
            ? path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(_host.WorkspacePath, path));

    private void FilesHeader_Click(object sender, RoutedEventArgs e)
    {
        _filesExpanded = !_filesExpanded;
        if (_filesExpanded)
        {
            // Opening the list is a sign the user is reading: don't auto-collapse under them.
            _doneTimer.Stop();
            _collapseTimer.Stop();
        }

        RenderChangedFiles();
        FilesChevronRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(_filesExpanded ? 0 : 90, _filesExpanded ? 90 : 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase() });
    }

    private void FileRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangedFile file)
        {
            file.Expanded = !file.Expanded;
            RenderChangedFiles();
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangedFile file)
        {
            EditorLauncher.Open(ResolvePath(file.Path), goTo: true);
        }
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        var workspace = _host.WorkspacePath;
        if (workspace.Length > 0 && Directory.Exists(workspace))
        {
            EditorLauncher.Open(workspace, goTo: false);
        }
        else if (_changedFiles.Values.FirstOrDefault(file => file.Kind != "delete") is { } first)
        {
            EditorLauncher.Open(ResolvePath(first.Path), goTo: true);
        }
    }
}

/// <summary>
/// Opens files or folders in VS Code when its <c>code</c> CLI is installed, otherwise with the
/// file's default app (folders in Explorer).
/// </summary>
internal static class EditorLauncher
{
    private static string? _codeCli;
    private static bool _searched;

    public static void Open(string path, bool goTo)
    {
        try
        {
            if (FindCodeCli() is { } cli)
            {
                // code.cmd is a batch file: run it through cmd without flashing a console.
                var args = goTo ? $"-g \"{path}\"" : $"\"{path}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{cli}\" {args}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("EditorLauncher", exception.Message);
        }
    }

    private static string? FindCodeCli()
    {
        if (_searched)
        {
            return _codeCli;
        }

        _searched = true;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => System.IO.Path.Combine(dir.Trim(), "code.cmd"))
            .Append(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft VS Code", "bin", "code.cmd"))
            .Append(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft VS Code", "bin", "code.cmd"));
        _codeCli = candidates.FirstOrDefault(File.Exists);
        return _codeCli;
    }
}
