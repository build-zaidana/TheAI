using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.Core;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexBar.App;

// Subscription usage. The idle notch carries a small ring for the rolling session window (the
// one that runs out first in practice); the launcher lists every window with a bar and the reset
// time. Colours only change when it matters: orange from 75% used, red from 90% or when a limit
// has been hit.
public partial class NotchWindow
{
    private static readonly Brush QuotaNormalBrush = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush QuotaTrackBrush = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    /// <summary>Re-read the host's quota (after a quota event or a provider switch).</summary>
    public void RefreshQuota()
    {
        var quota = _host.Quota;
        var windows = quota?.Windows.Select(Current).ToArray() ?? [];

        // Ring: the session window, or whatever is closest to its limit.
        var ring = windows.FirstOrDefault(w => w.Id == "session") ?? windows.MaxBy(w => w.UsedFraction);
        if (ring is null)
        {
            QuotaArc.Data = null;
            CompactPanel.ToolTip = null;
        }
        else
        {
            var used = quota!.LimitReached && ring.UsedFraction >= 0.99 ? 1 : ring.UsedFraction;
            QuotaArc.Data = ArcGeometry(used);
            QuotaArc.Stroke = QuotaBrush(used, quota.LimitReached);
            CompactPanel.ToolTip = string.Join("\n", windows.Select(w => $"{WindowLabel(w.Id)}: {QuotaText(w, quota.LimitReached)}"));
        }

        QuotaPanel.Children.Clear();
        foreach (var window in windows)
        {
            QuotaPanel.Children.Add(BuildQuotaRow(window, quota!.LimitReached));
        }

        QuotaPanel.Visibility = windows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyQuotaRing();
    }

    private void ApplyQuotaRing() =>
        QuotaRing.Visibility = _state == NotchState.Idle && QuotaArc.Data is not null ? Visibility.Visible : Visibility.Collapsed;

    // A window whose reset time has passed has started over; don't keep showing the old number.
    private static ProviderQuotaWindow Current(ProviderQuotaWindow window) =>
        window.ResetsAt is { } resets && resets <= DateTimeOffset.Now ? window with { UsedFraction = 0, ResetsAt = null } : window;

    private FrameworkElement BuildQuotaRow(ProviderQuotaWindow window, bool limitReached)
    {
        var used = window.UsedFraction;
        var brush = QuotaBrush(used, limitReached && used >= 0.99);

        var grid = new Grid { Height = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });

        grid.Children.Add(new TextBlock
        {
            Text = WindowLabel(window.Id),
            FontSize = 11,
            Foreground = (Brush)FindResource("NotchText3"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(used, 0.001), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - used, 0.001), GridUnitType.Star) });
        var track = new Border { Background = QuotaTrackBrush, CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { Background = brush, CornerRadius = new CornerRadius(2), MinWidth = used > 0 ? 4 : 0 });
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var text = new TextBlock
        {
            Text = QuotaText(window, limitReached),
            FontSize = 11,
            Foreground = used >= 0.75 ? brush : (Brush)FindResource("NotchText2"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    private Brush QuotaBrush(double used, bool limitReached) =>
        limitReached || used >= 0.9 ? (Brush)FindResource("NotchRed")
        : used >= 0.75 ? (Brush)FindResource("NotchOrange")
        : QuotaNormalBrush;

    private static string WindowLabel(string id) => id switch
    {
        "session" => L("notch.quota.session"),
        "weekly" => L("notch.quota.weekly"),
        "seven_day_opus" => L("notch.quota.weeklyOpus"),
        "seven_day_sonnet" => L("notch.quota.weeklySonnet"),
        _ => id.Replace('_', ' ')
    };

    private static string QuotaText(ProviderQuotaWindow window, bool limitReached)
    {
        var amount = limitReached && window.UsedFraction >= 0.99
            ? L("notch.quota.reached")
            : string.Format(L("notch.quota.used"), Math.Round(window.UsedFraction * 100));
        return window.ResetsAt is { } resets
            ? amount + " · " + string.Format(L("notch.quota.resets"), FormatReset(resets))
            : amount;
    }

    // "21.30" when it resets within a day, otherwise "Sab 09.00".
    private static string FormatReset(DateTimeOffset resets)
    {
        var local = resets.ToLocalTime();
        var culture = CodexBar.Core.Localization.Instance.Language == AppLanguage.En
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("id-ID");
        return local - DateTimeOffset.Now < TimeSpan.FromHours(20)
            ? local.ToString("t", culture)
            : local.ToString("ddd ", culture) + local.ToString("t", culture);
    }

    // Clockwise from 12 o'clock on the 14px indicator.
    private static Geometry ArcGeometry(double fraction)
    {
        const double centre = 7, radius = 5.9;
        if (fraction >= 0.999)
        {
            return new EllipseGeometry(new Point(centre, centre), radius, radius);
        }

        fraction = Math.Max(fraction, 0.02);
        var angle = fraction * 2 * Math.PI;
        var end = new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(centre, centre - radius) };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
