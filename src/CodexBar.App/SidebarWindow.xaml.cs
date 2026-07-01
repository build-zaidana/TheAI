using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

public partial class SidebarWindow : Window
{
    private readonly ConversationController _controller;
    private readonly Func<ProviderThreadSummary, Task> _openThread;
    private readonly Func<Task> _createThread;
    private bool _dark;
    private IReadOnlyList<ProviderThreadSummary> _threads = [];
    private bool _openingThread;
    private bool _docked;
    private CancellationTokenSource? _searchCts;

    public SidebarWindow(
        ConversationController controller,
        Func<ProviderThreadSummary, Task> openThread,
        Func<Task> createThread,
        bool dark = true)
    {
        _controller = controller;
        _openThread = openThread;
        _createThread = createThread;
        _dark = dark;
        InitializeComponent();
        _controller.EventReceived += Controller_EventReceived;
        Closed += (_, _) =>
        {
            _controller.EventReceived -= Controller_EventReceived;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        };
    }

    public void ShowNextTo(Window owner, bool docked)
    {
        _docked = docked;
        Owner = owner;
        Topmost = owner.Topmost;
        Show();
        SnapTo(owner);
        Activate();
    }

    /// <summary>Tempelkan ke sisi owner (mode docked) atau posisikan sekali (separated).</summary>
    public void SnapTo(Window owner)
    {
        var workArea = WindowPositioner.GetWorkArea(owner);
        var gap = _docked ? 6.0 : 12.0;
        var preferredLeft = owner.Left - ActualWidth - gap;
        Left = preferredLeft >= workArea.Left
            ? preferredLeft
            : Math.Min(workArea.Right - ActualWidth, owner.Left + owner.ActualWidth + gap);

        if (_docked)
        {
            // Sejajarkan tepi bawah dengan owner agar terasa menyatu.
            Top = Math.Clamp(
                owner.Top + owner.ActualHeight - ActualHeight,
                workArea.Top,
                Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        }
        else
        {
            Top = Math.Clamp(owner.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_dark);
        await RefreshAsync();
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        var background = dark
            ? Color.FromRgb(23, 25, 29)
            : Color.FromRgb(247, 248, 250);
        Resources["PanelBackground"] = new SolidColorBrush(background);
        Resources["CardBackground"] = new SolidColorBrush(
            dark ? Color.FromRgb(31, 34, 40) : Color.FromRgb(255, 255, 255));
        Resources["PrimaryText"] = new SolidColorBrush(
            dark ? Color.FromRgb(244, 246, 250) : Color.FromRgb(27, 34, 48));
        Resources["MutedText"] = new SolidColorBrush(
            dark ? Color.FromRgb(208, 214, 224) : Color.FromRgb(88, 98, 117));
        RootBorder.BorderBrush = new SolidColorBrush(
            dark ? Color.FromRgb(58, 63, 72) : Color.FromRgb(210, 215, 223));
        WindowBackdrop.ApplySolid(this, dark, background);
    }

    private async Task RefreshAsync()
    {
        FooterText.Text = "Memuat akun dan riwayat…";
        try
        {
            var accountTask = _controller.GetAccountAsync();
            var threadsTask = _controller.ListThreadsAsync();
            await Task.WhenAll(accountTask, threadsTask);

            UpdateAccount(await accountTask);
            _threads = await threadsTask;
            ApplyFilter();
            FooterText.Text = $"{_threads.Count} percakapan lokal Codex";
        }
        catch (Exception exception)
        {
            FooterText.Text = exception.Message;
            AccountNameText.Text = "Status akun tidak tersedia";
            AccountPlanText.Text = "Periksa koneksi Codex";
            AccountDot.Fill = new SolidColorBrush(Color.FromRgb(232, 104, 104));
        }
    }

    private void UpdateAccount(ProviderAccountInfo account)
    {
        if (!account.IsAuthenticated)
        {
            AccountNameText.Text = "Belum login";
            AccountPlanText.Text = "Jalankan codex login";
            AccountDot.Fill = new SolidColorBrush(Color.FromRgb(232, 104, 104));
            return;
        }

        AccountNameText.Text = account.Email ?? "Akun Codex aktif";
        AccountPlanText.Text = account.AuthenticationMethod == "chatgpt"
            ? $"ChatGPT · {FormatPlan(account.PlanType)}"
            : "OpenAI API key";
        AccountDot.Fill = new SolidColorBrush(Color.FromRgb(94, 230, 168));
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filtered = _threads
            .Where(thread => query.Length == 0 ||
                thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.Preview.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.WorkspacePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(thread => new SidebarThreadItem(thread))
            .ToList();

        ThreadList.ItemsSource = filtered;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Controller_EventReceived(object? sender, ProviderEvent providerEvent)
    {
        if (providerEvent is not ProviderThreadStatusEvent status)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _threads = _threads.Select(thread => thread.Id == status.ThreadId
                ? thread with { Status = status.Status, ActiveFlags = status.ActiveFlags }
                : thread).ToArray();
            ApplyFilter();
        });
    }

    private async void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_openingThread || ThreadList.SelectedItem is not SidebarThreadItem item)
        {
            return;
        }

        _openingThread = true;
        ThreadList.IsEnabled = false;
        FooterText.Text = "Membuka percakapan…";
        try
        {
            await _openThread(item.Thread);
            FooterText.Text = "Percakapan aktif di CodexBar";
        }
        catch (Exception exception)
        {
            FooterText.Text = exception.Message;
        }
        finally
        {
            ThreadList.SelectedItem = null;
            ThreadList.IsEnabled = true;
            _openingThread = false;
        }
    }

    private async void NewThreadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FooterText.Text = "Membuat percakapan…";
            await _createThread();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            FooterText.Text = exception.Message;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();

        // Kosong → tampilkan daftar terbaru (lokal). Ada query → cari seluruh riwayat via server.
        if (query.Length == 0)
        {
            _searchCts?.Cancel();
            ApplyFilter();
            FooterText.Text = $"{_threads.Count} percakapan lokal Codex";
            return;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            await Task.Delay(280, cts.Token);
            var results = await _controller.SearchThreadsAsync(query, 50, cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            var items = results.Select(thread => new SidebarThreadItem(thread)).ToList();
            ThreadList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FooterText.Text = items.Count == 0
                ? $"Tidak ada hasil untuk “{query}”"
                : $"{items.Count} hasil untuk “{query}”";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FooterText.Text = exception.Message;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static string FormatPlan(string? planType) => planType switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        "free" => "Free",
        null or "" => "aktif",
        _ => planType.Replace('_', ' ')
    };

    private sealed class SidebarThreadItem
    {
        public SidebarThreadItem(ProviderThreadSummary thread)
        {
            Thread = thread;
        }

        public ProviderThreadSummary Thread { get; }
        public string Title => Thread.Title;
        public string WorkspaceName => Path.GetFileName(Thread.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : Thread.WorkspacePath;
        public string UpdatedLabel => FormatUpdatedAt(Thread.UpdatedAt);
        public string StatusLabel => Thread.Status switch
        {
            "active" when Thread.ActiveFlags?.Contains("waitingOnApproval") == true => "Perlu izin",
            "active" when Thread.ActiveFlags?.Contains("waitingOnUserInput") == true => "Perlu jawaban",
            "active" => "Bekerja",
            "systemError" => "Error",
            _ => string.Empty
        };
        public Brush StatusBrush => Thread.Status switch
        {
            "active" when Thread.ActiveFlags is { Count: > 0 } =>
                new SolidColorBrush(Color.FromRgb(230, 180, 94)),
            "active" => new SolidColorBrush(Color.FromRgb(94, 180, 230)),
            "systemError" => new SolidColorBrush(Color.FromRgb(232, 104, 104)),
            _ => Brushes.Transparent
        };

        private static string FormatUpdatedAt(DateTimeOffset value)
        {
            if (value == DateTimeOffset.MinValue)
            {
                return string.Empty;
            }

            var local = value.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            return local.Date == today
                ? local.ToString("HH:mm")
                : local.Date == today.AddDays(-1)
                    ? "Kemarin"
                    : local.ToString("dd MMM");
        }
    }
}
