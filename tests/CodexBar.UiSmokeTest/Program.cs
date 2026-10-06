using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.App;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Windows;
using AppLocalization = CodexBar.Core.Localization;

namespace CodexBar.UiSmokeTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CodexBar.App;component/Theme.xaml")
            });
            TestConversationPanel();
            TestMarkdownFormatting();
            TestMessageActions();
            TestAllApprovalDecisions();
            TestStructuredInputForm();
            TestUrlElicitation();
            TestTemplateManagerDialog();
            TestQuickCaptureClipboardGuard();
            TestCodeBlockCopyButton();
            Console.WriteLine("PASS WPF approval=4 request-user-input=true mcp-form=true mcp-url=true templates=true quick-capture=true code-copy=true");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {exception}");
            return 1;
        }
    }

    private static void TestQuickCaptureClipboardGuard()
    {
        Assert(SelectionCapture.NormalizeCapturedText(10, 10, "teks lama") is null,
            "Quick capture tidak boleh memakai isi clipboard lama jika sequence tidak berubah.");
        Assert(SelectionCapture.NormalizeCapturedText(10, 11, "  teks terpilih  ") == "teks terpilih",
            "Quick capture harus menerima dan merapikan selection baru.");
        Assert(SelectionCapture.NormalizeCapturedText(10, 11, "   ") is null,
            "Quick capture harus mengabaikan selection kosong.");
    }

    private static void TestConversationPanel()
    {
        var panel = new ConversationPanel();
        panel.Show();
        panel.UpdateLayout();
        string? sent = null;
        var stopped = false;
        panel.SendRequested += (_, text) => sent = text;
        panel.StopRequested += (_, _) => stopped = true;

        panel.Input.Text = "  jalankan test  ";
        panel.SendButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert(sent == "jalankan test" && panel.Input.Text.Length == 0,
            "Panel percakapan harus mengirim teks input (dirapikan) lalu mengosongkannya.");

        panel.SetBusy(true);
        panel.SendButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert(stopped, "Saat agent bekerja dan input kosong, tombol kirim panel harus menghentikan giliran.");

        // Grow out of a notch, wait for the motion to finish, then widen.
        panel.Hide();
        var opened = false;
        panel.Opened += (_, _) => opened = true;
        panel.NotchRect = () => (new Rect(SystemParameters.WorkArea.Right - 200, 0, 150, 32), new CornerRadius(0, 0, 14, 14));
        panel.ShowNear(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!opened && DateTime.UtcNow < deadline)
        {
            Pump();
        }

        Assert(opened, "Panel percakapan harus selesai terbuka.");
        panel.UpdateLayout();
        var narrow = panel.Panel.ActualWidth;
        Click(panel.ExpandButton);
        for (var i = 0; i < 90; i++)
        {
            Pump();
        }

        panel.UpdateLayout();
        Assert(panel.Panel.ActualWidth > narrow + 100,
            $"Tombol perlebar harus melebarkan panel ({narrow:0} -> {panel.Panel.ActualWidth:0}).");

        // "/" lists the commands; Enter runs the highlighted one.
        string? command = null;
        panel.CommandInvoked += (_, name) => command = name;
        panel.Input.Text = "/cl";
        Pump();
        panel.Input.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel.Input), 0, System.Windows.Input.Key.Enter)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        });
        Assert(command == "clear" && panel.Input.Text.Length == 0, $"Perintah /clear harus dijalankan dari daftar perintah (dapat: {command}).");

        // A notice can carry a link that fixes it ("Lanjutkan di sini").
        var continued = false;
        panel.SetNotice("Sesi ini sedang dipakai di Claude Code.", "Lanjutkan di sini →", () => continued = true);
        var link = panel.NoticeText.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single();
        link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        Assert(continued && panel.NoticeText.Visibility == Visibility.Visible, "Tautan di peringatan harus menjalankan aksinya.");
        panel.SetNotice(null);

        // The ring under the box shows the context used.
        panel.SetContextUsage(50_000, 200_000);
        Assert(panel.ContextArc.Data is not null && panel.ContextRing.ToolTip?.ToString()?.Contains("25%") == true,
            $"Lingkaran konteks harus menunjukkan pemakaian konteks (tooltip: {panel.ContextRing.ToolTip}).");

        // Clicking the ring shows the context card with Compact.
        var compacted = false;
        panel.CompactRequested += (_, _) => compacted = true;
        var card = panel.BuildContextCard(() => { });
        card.Measure(new Size(400, 400));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        Click(FindVisualChildren<Button>(card).Single());
        Assert(compacted && FindVisualChildren<TextBlock>(card).Any(t => t.Text.Contains("150K")),
            "Kartu konteks harus menampilkan sisa token dan tombol Compact yang berfungsi.");
        var snapshots = Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(snapshots))
        {
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(card);
            var cardPng = new System.Windows.Media.Imaging.PngBitmapEncoder();
            cardPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = System.IO.File.Create(System.IO.Path.Combine(snapshots, "context.png"));
            cardPng.Save(output);
        }

        // Model / effort / mode under the box show the chosen option's label.
        panel.SetComposerChoices(
            [new ConversationPanel.Choice("opus", "Opus 5.5"), new ConversationPanel.Choice("sonnet", "Sonnet 5.5")], "opus", "Claude",
            [new ConversationPanel.Choice("", "Default"), new ConversationPanel.Choice("medium", "Medium")], "medium",
            [new ConversationPanel.Choice("on-request", "Auto")], "on-request");
        Assert(panel.ModelText.Text == "Opus 5.5" && panel.EffortText.Text == "Medium" && panel.ModeText.Text == "Auto",
            "Baris di bawah kotak input harus menampilkan model, effort, dan mode yang dipilih.");

        // Attachments show as chips in the composer: a thumbnail for an image, a pill for a file.
        var picture = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexbar-ui-thumb.png");
        var pixels = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null,
            new byte[] { 0x57, 0x77, 0xD9, 0, 0x57, 0x77, 0xD9, 0, 0xFF, 0xFF, 0xFF, 0, 0x30, 0x30, 0x30, 0 }, 8);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pixels));
        using (var stream = System.IO.File.Create(picture))
        {
            png.Save(stream);
        }

        string? removed = null;
        panel.RemoveAttachmentRequested += (_, path) => removed = path;
        panel.SetAttachments([("Screenshot clipboard.png", picture), ("notes.txt", @"C:\nope\notes.txt")]);
        Pump();
        panel.UpdateLayout();
        Assert(panel.AttachmentChips.Children.Count == 2 && panel.SendButton.IsEnabled, "Lampiran harus tampil sebagai chip dan bisa dikirim.");
        Click(FindVisualChildren<Button>(panel.AttachmentChips).First());
        Assert(removed == picture, "Tombol × pada chip harus menghapus lampirannya.");

        var dir = Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            panel.Input.Text = "Tolong cek screenshot ini";
            Pump();
            var composer = (FrameworkElement)((FrameworkElement)panel.AttachmentChips.Parent).Parent;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)panel.Panel.ActualWidth, (int)panel.Panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel.Panel);
            var at = composer.TransformToAncestor(panel.Panel).Transform(new Point(0, 0));
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(bitmap, new Int32Rect(
                0, (int)Math.Max(0, at.Y - 12), (int)panel.Panel.ActualWidth, (int)Math.Min(panel.Panel.ActualHeight - Math.Max(0, at.Y - 12), composer.ActualHeight + 50)));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, "panel.png"));
            encoder.Save(file);
        }

        panel.PrepareForExit();
        panel.Close();
    }

    private static void TestMarkdownFormatting()
    {
        const string markdown = """
            ## Ringkasan

            Teks dengan **tebal**, *miring*, ~~coret~~ dan `kode`.

            - Satu
              - Dua bertingkat
                - Tiga
            3. Mulai dari tiga
            4. Empat
            - [ ] Belum
            - [x] Sudah

            ---

            | Nama | Nilai |
            |------|------:|
            | a    | 1     |
            | b    | 22    |

            ```go
            // komentar
            func main() { fmt.Println("halo", 42) }
            ```
            """;
        var style = new MarkdownStyle(Brushes.White, Brushes.Gray, Brushes.White, Brushes.White,
            new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            Brushes.Gray, new FontFamily("Cascadia Mono, Consolas"), 14, false);
        var panel = new StackPanel();
        panel.Children.Add(ActivityList.Build(
        [
            new ProviderActivityStep("Edited", "CodeHighlighter.cs", "C:/x/CodeHighlighter.cs", Failed: true),
            new ProviderActivityStep("Ran", "a command", "dotnet build"),
            new ProviderActivityStep("Read", "markdown.png", "C:/x/markdown.png"),
            new ProviderActivityStep(string.Empty, "Tinggal satu perbaikan kecil: font simbol hanya dipakai untuk kotak checklist, supaya penanda daftar biasa tidak ikut mengecil.", "Tinggal satu perbaikan kecil: font simbol hanya dipakai untuk kotak checklist, supaya penanda daftar biasa tidak ikut mengecil.", IsText: true)
        ], style));
        panel.Children.Add(MarkdownRenderer.BuildPanel(markdown, style));
        var summary = ActivityList.BuildSummary(
        [
            new ProviderActivityStep("Edited", "CodeHighlighter.cs", "C:/x/CodeHighlighter.cs", Added: 104,
                Edits: [new ProviderTextEdit(string.Join('\n', "var a = 1;", "var b = 2;", "Print(a);"), string.Join('\n', "var a = 1;", "var b = 3;", "Print(a);", "Print(b);"))]),
            new ProviderActivityStep("Edited", "MarkdownRenderer.cs", "C:/x/MarkdownRenderer.cs", Added: 8, Removed: 4),
            new ProviderActivityStep("Edited", "Broken.cs", "C:/x/Broken.cs", Failed: true, Added: 9)
        ], style);
        Assert(summary is not null, "Giliran yang mengubah file harus punya ringkasan.");
        panel.Children.Add(summary!);
        var host = new Border { Background = Brushes.Black, Padding = new Thickness(16), Width = 440, Child = panel };
        host.Measure(new Size(440, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        // Clicking an edited file opens its diff under the row.
        var fileRow = FindVisualChildren<Border>(summary!).First(b => Equals(b.ToolTip, "C:/x/CodeHighlighter.cs"));
        fileRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        });
        host.Measure(new Size(440, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Assert(FindVisualChildren<TextBlock>(summary!).Any(t => t.Text == "+ var b = 3;") &&
               FindVisualChildren<TextBlock>(summary!).Any(t => t.Text == "- var b = 2;"),
            "Klik file di ringkasan harus membuka diff-nya.");

        var texts = FindVisualChildren<TextBlock>(host).Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).ToArray();
        Assert(texts.Contains("3."), "Daftar bernomor harus memakai nomor aslinya.");
        Assert(texts.Any(t => t.StartsWith("+112 −4")) && texts.Contains("Edited 2 files") && !texts.Contains("Broken.cs"), "Ringkasan harus menjumlahkan file yang berhasil saja.");
        Assert(texts.Contains("◦") && texts.Contains("☐") && texts.Contains("☑"), "Daftar bertingkat dan checklist harus punya penanda.");
        Assert(texts.Contains("Nama") && texts.Contains("22"), "Tabel harus dirender per sel.");
        Assert(!texts.Any(t => t.Contains("|---") || t.Trim() == "---"), "Pemisah tabel dan garis tidak boleh tampil sebagai teks.");

        var dir = Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, "markdown.png"));
            encoder.Save(file);
        }
    }

    private static void TestMessageActions()
    {
        var style = new MarkdownStyle(Brushes.White, Brushes.Gray, Brushes.White, Brushes.White,
            new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            Brushes.Gray, new FontFamily("Consolas"), 14, false);
        var theme = new ChatTheme(style, new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Brushes.White,
            Brushes.Transparent, Brushes.Gray, Brushes.Gray, false);
        var view = new ConversationView();
        var window = new Window { Width = 460, Height = 400, Content = view, Background = Brushes.Black, ShowInTaskbar = false, Left = -3000 };
        window.Show();
        var question = new ChatMessage { IsUser = true, Markdown = "Tolong cek ini\n\n- 📎 **foto.png**" };
        var answer = new ChatMessage { IsUser = false, Markdown = "Sudah dicek. Lihat [Foo.cs](src/Foo.cs:42).", Time = DateTimeOffset.Now };
        view.Render([question, answer], theme);
        window.UpdateLayout();

        string? edited = null;
        ChatMessage? retried = null;
        view.EditRequested += (_, message) => edited = message.Markdown;
        view.RetryRequested += (_, message) => retried = message;
        var buttons = FindVisualChildren<Button>(view).ToArray();
        string Name(Button b) => AutomationProperties.GetName(b);
        Assert(buttons.Count(b => Name(b) == AppLocalization.Instance["conversation.copyName"]) == 2 &&
               buttons.Any(b => Name(b) == "Edit") && buttons.Any(b => Name(b) is "Coba lagi" or "Retry"),
            $"Pesan harus punya tombol ikon salin, edit, dan coba lagi: {string.Join(", ", buttons.Select(Name))}");
        Assert(buttons.Where(b => Name(b) is "Edit" or "Coba lagi" or "Retry" || Name(b) == AppLocalization.Instance["conversation.copyName"])
            .All(b => b.Content is System.Windows.Shapes.Path), "Tombol di bawah pesan harus ikon saja, tanpa tulisan.");
        Click(buttons.First(b => Name(b) == "Edit"));
        Click(buttons.First(b => Name(b) is "Coba lagi" or "Retry"));
        Assert(edited == question.Markdown && retried == answer, "Edit dan coba lagi harus diteruskan ke host.");
        Assert(buttons.Any(b => Name(b) is "Lainnya" or "More"), "Jawaban harus punya menu lainnya (fork, pin, read aloud).");

        // Versions of a prompt: "‹ 2/2 ›" under it; the arrows ask the host for the other version.
        (ChatMessage Message, int Step)? versionStep = null;
        view.VersionOf = message => message == question ? (1, 2) : null;
        view.VersionRequested += (_, request) => versionStep = request;
        view.Render([question, answer], theme);
        window.UpdateLayout();
        Assert(FindVisualChildren<TextBlock>(view).Any(t => t.Text == "2/2"), "Prompt dengan dua versi harus menampilkan 2/2.");
        Click(FindVisualChildren<Button>(view).First(b => AutomationProperties.GetName(b) is "Versi sebelumnya" or "Previous version"));
        Assert(versionStep is { Step: -1 } && versionStep.Value.Message == question, "Panah versi harus meminta versi sebelumnya.");
        view.VersionOf = null;
        view.Render([question, answer], theme);
        window.UpdateLayout();

        // Find: the matching messages, framed, with the words marked.
        var matches = view.FindMatches("dicek");
        Assert(matches.SequenceEqual(new[] { 1 }), $"Pencarian harus menemukan pesan yang cocok: {string.Join(",", matches)}");

        // Time on hover, and file links that the host opens.
        Assert(FindVisualChildren<TextBlock>(view).Any(t => t.Text == answer.Time!.Value.ToLocalTime().ToString("HH:mm")),
            "Jawaban harus punya label waktu.");
        string? opened = null;
        Action<string> onLink = target => opened = target;
        MarkdownRenderer.FileLinkClicked += onLink;
        var fileLink = FindVisualChildren<TextBlock>(view).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).First();
        fileLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        MarkdownRenderer.FileLinkClicked -= onLink;
        Assert(opened == "src/Foo.cs:42", $"Tautan file harus diteruskan ke host (dapat: {opened}).");
        view.ShowMatch(1, "dicek");
        window.UpdateLayout();
        Assert(FindVisualChildren<TextBlock>(view).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>())
                .Any(r => r.Text.Equals("dicek", StringComparison.OrdinalIgnoreCase) && r.Background is not null),
            "Kata yang dicari harus ditandai di pesannya.");
        view.ClearSearch();

        // Toasts: valid XML with one button per action (a broken one would silence every toast).
        var toast = new System.Xml.XmlDocument();
        toast.LoadXml(CompletionNotifier.ToastXml("Konteks 92% & <penuh>", "Compact \"sekarang\"", "context", false,
            [("Compact", "compact"), ("Lihat", "view")]));
        Assert(toast.SelectNodes("//action")!.Count == 2 && toast.SelectSingleNode("//action")!.Attributes!["arguments"]!.Value == "action=compact;key=context",
            "XML notifikasi harus valid dan berisi tombol-tombolnya.");

        // Export: the conversation as Markdown.
        var exported = ConversationExport.Build("Uji", "Claude", [question, answer]);
        Assert(exported.StartsWith("# Uji") && exported.Contains("Tolong cek ini") && exported.Contains("Sudah dicek."),
            "Ekspor harus berisi judul dan pesan-pesannya.");

        // Pin as chapter: a divider with the title above the pinned message.
        view.PinnedKeys = new HashSet<string> { ConversationView.KeyOf(answer) };
        view.Render([question, answer], theme);
        window.UpdateLayout();
        Assert(FindVisualChildren<TextBlock>(view).Any(t => t.Text == ConversationView.ChapterTitle(answer) && t.FontWeight == FontWeights.SemiBold),
            "Pesan yang di-pin harus diberi pembatas chapter berjudul.");
        var spoken = ReadAloud.Plain("""
            ## Judul
            **tebal** dan `kode`
            ```
            x = 1
            ```
            - [tautan](http://a)
            """);
        Assert(!spoken.Contains('#') && !spoken.Contains('*') && !spoken.Contains('`') && !spoken.Contains("x = 1") &&
               spoken.Contains("Judul") && spoken.Contains("tebal dan kode") && spoken.Contains("tautan") && !spoken.Contains("http"),
            $"Teks yang dibacakan harus tanpa tanda markdown dan kode: '{spoken}'");

        // The Windows voice makes audio for it (made only, not played).
        using (var synthesizer = new global::Windows.Media.SpeechSynthesis.SpeechSynthesizer())
        {
            var audio = synthesizer.SynthesizeTextToStreamAsync(spoken).AsTask().GetAwaiter().GetResult();
            Assert(audio.Size > 1000, $"Suara Windows harus menghasilkan audio untuk Read aloud (ukuran {audio.Size}).");
        }

        var dir = Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            foreach (var host in FindVisualChildren<Border>(view).Where(b => b.MaxWidth < double.PositiveInfinity))
            {
                host.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            }
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(440, 220, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 440, 220));
                context.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, 440, 220));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, "actions.png"));
            encoder.Save(file);
        }

        window.Close();
    }

    private static void TestAllApprovalDecisions()
    {
        var expected = new Dictionary<string, ProviderApprovalDecision>
        {
            ["Tolak & hentikan"] = ProviderApprovalDecision.Cancel,
            ["Tolak"] = ProviderApprovalDecision.Decline,
            ["Izinkan sesi"] = ProviderApprovalDecision.AcceptForSession,
            ["Izinkan sekali"] = ProviderApprovalDecision.AcceptOnce
        };

        foreach (var (label, decision) in expected)
        {
            using var document = JsonDocument.Parse("""{"threadId":"ui-smoke"}""");
            var approval = new ProviderApprovalEvent(
                "item/commandExecution/requestApproval",
                "Perintah: dotnet test\nDirektori: C:\\workspace",
                _ => Task.CompletedTask,
                document.RootElement.Clone());
            var dialog = new ApprovalRequestDialog(approval);
            dialog.Loaded += (_, _) =>
            {
                var buttons = FindVisualChildren<Button>(dialog).ToArray();
                Assert(expected.Keys.All(name => buttons.Any(button => Equals(button.Content, name))),
                    "Dialog approval tidak menampilkan empat keputusan.");
                Click(buttons.Single(button => Equals(button.Content, label)));
            };
            Assert(dialog.ShowDialog() == true && dialog.Decision == decision,
                $"Tombol {label} menghasilkan keputusan yang salah.");
        }
    }

    private static void TestStructuredInputForm()
    {
        using var document = JsonDocument.Parse("""{"threadId":"ui-smoke","mode":"form"}""");
        var request = new ProviderUserInputEvent(
            "Input Codex",
            "Isi konfigurasi pengujian.",
            [
                new ProviderInputQuestion("strategy", "Strategi", "Pilih strategi",
                    [new ProviderInputOption("Aman", "Perubahan kecil"), new ProviderInputOption("Cepat", "Perubahan luas")],
                    AllowsOther: true),
                new ProviderInputQuestion("tags", "Tag", "Pilih tag",
                    [new ProviderInputOption("one", ""), new ProviderInputOption("two", "")],
                    AllowsMultiple: true),
                new ProviderInputQuestion("count", "Jumlah", "Masukkan jumlah", [], ValueType: "integer"),
                new ProviderInputQuestion("secret", "Rahasia", "Masukkan rahasia", [], IsSecret: true)
            ],
            _ => Task.CompletedTask,
            document.RootElement.Clone());

        var dialog = new InteractiveRequestDialog(request);
        dialog.Loaded += (_, _) =>
        {
            var controls = FindVisualChildren<FrameworkElement>(dialog).ToArray();
            var strategy = controls.OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "Strategi");
            strategy.SelectedIndex = 1;
            var tags = controls.OfType<ListBox>().Single(control => AutomationProperties.GetName(control) == "Tag");
            ((ListBoxItem)tags.Items[0]).IsSelected = true;
            ((ListBoxItem)tags.Items[1]).IsSelected = true;
            controls.OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Jumlah").Text = "3";
            controls.OfType<PasswordBox>().Single(control => AutomationProperties.GetName(control) == "Rahasia").Password = "token-test";
            Click(controls.OfType<Button>().Single(button => Equals(button.Content, "Lanjutkan")));
        };

        Assert(dialog.ShowDialog() == true, "Form input terstruktur tidak dapat dikirim.");
        Assert(dialog.Answers["strategy"].Single() == "Cepat" &&
               dialog.Answers["tags"].SequenceEqual(["one", "two"]) &&
               dialog.Answers["count"].Single() == "3" &&
               dialog.Answers["secret"].Single() == "token-test",
            "Form input terstruktur tidak mempertahankan jawaban.");
    }

    private static void TestUrlElicitation()
    {
        using var document = JsonDocument.Parse("""{"threadId":"ui-smoke","mode":"url"}""");
        var request = new ProviderUserInputEvent(
            "Hubungkan layanan",
            "Buka halaman otorisasi lalu lanjutkan.",
            [],
            _ => Task.CompletedTask,
            document.RootElement.Clone(),
            ActionUrl: "https://example.com/authorize");
        var dialog = new InteractiveRequestDialog(request);
        dialog.Loaded += (_, _) =>
        {
            var buttons = FindVisualChildren<Button>(dialog).ToArray();
            Assert(buttons.Any(button => Equals(button.Content, "Buka example.com")),
                "MCP URL tidak menampilkan tombol URL yang eksplisit.");
            Click(buttons.Single(button => Equals(button.Content, "Lanjutkan")));
        };
        Assert(dialog.ShowDialog() == true, "MCP URL tidak dapat dikonfirmasi.");
    }

    private static void TestTemplateManagerDialog()
    {
        AppLocalization.Instance.SetLanguage(AppLanguage.Id);
        var dialog = new TemplateManagerDialog(
        [
            new PromptTemplate { Name = "Awal", Content = "Isi awal" }
        ]);
        dialog.Loaded += (_, _) =>
        {
            var controls = FindVisualChildren<FrameworkElement>(dialog).ToArray();
            var name = controls.OfType<TextBox>()
                .Single(control => AutomationProperties.GetName(control) == "Nama template");
            var content = controls.OfType<TextBox>()
                .Single(control => AutomationProperties.GetName(control) == "Isi template");
            var list = controls.OfType<ListBox>()
                .Single(control => AutomationProperties.GetName(control) == "Daftar template prompt");
            var buttons = controls.OfType<Button>().ToArray();

            name.Text = "Edit";
            content.Text = "Isi {selection}";
            Click(buttons.Single(button => Equals(button.Content, "Simpan")));

            Click(buttons.Single(button => Equals(button.Content, "Baru")));
            name.Text = "Baru";
            content.Text = "Clipboard {clipboard}";
            Click(buttons.Single(button => Equals(button.Content, "Simpan")));

            list.SelectedIndex = 0;
            Click(buttons.Single(button => Equals(button.Content, "Hapus")));
            Click(buttons.Single(button => Equals(button.Content, "Selesai")));
        };

        Assert(dialog.ShowDialog() == true, "Dialog kelola template tidak bisa diselesaikan.");
        Assert(dialog.Templates.Count == 1 &&
               dialog.Templates[0].Name == "Baru" &&
               dialog.Templates[0].Content == "Clipboard {clipboard}",
            "Dialog kelola template harus bisa edit, tambah, hapus, lalu mengembalikan hasil final.");
    }

    private static void TestCodeBlockCopyButton()
    {
        var style = new MarkdownStyle(
            Brushes.White, Brushes.Gray, Brushes.LightBlue, Brushes.White,
            Brushes.Black, Brushes.Gray, Brushes.Gray,
            new FontFamily("Consolas"), 14, true);
        var panel = MarkdownRenderer.BuildPanel("```csharp\nvar x = 1;\n```", style);
        panel.Measure(new Size(800, 600));
        panel.Arrange(new Rect(0, 0, 800, 600));

        var copyButton = FindVisualChildren<Button>(panel)
            .FirstOrDefault(button => AutomationProperties.GetName(button) == AppLocalization.Instance["code.copy"]);
        Assert(copyButton is not null, "Blok kode harus menampilkan tombol Copy.");

        // Clicking must never throw (clipboard access is best-effort/guarded).
        Click(copyButton!);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Thread.Sleep(16);
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
