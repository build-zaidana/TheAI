using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.App;
using CodexBar.Providers;

namespace CodexBar.UiSmokeTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            TestAllApprovalDecisions();
            TestStructuredInputForm();
            TestUrlElicitation();
            Console.WriteLine("PASS WPF approval=4 request-user-input=true mcp-form=true mcp-url=true");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {exception}");
            return 1;
        }
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
