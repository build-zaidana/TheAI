using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Weather and the world clock as a page of the notch: today's weather where the user is (the city
// set in Settings, through Open-Meteo — no key, nothing but the city and its coordinates sent),
// and up to four other time zones. Refreshed every 30 minutes, only while the feature is on.
public partial class NotchWindow
{
    private const double WeatherWidth = 420;

    private static readonly Geometry SunGlyph = Icons.Sun;
    private static readonly Geometry MoonGlyph = Icons.Moon;
    private static readonly Geometry CloudGlyph = Icons.Cloud;
    private static readonly Geometry PartlyGlyph = Icons.CloudSun;
    private static readonly Geometry RainGlyph = Icons.CloudRain;
    private static readonly Geometry SnowGlyph = Icons.CloudSnow;
    private static readonly Geometry StormGlyph = Icons.CloudLightning;
    private static readonly Geometry FogGlyph = Icons.CloudFog;

    private readonly DispatcherTimer _weatherRefresh = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _clockTick = new() { Interval = TimeSpan.FromSeconds(20) };
    private GeoPlace? _place;
    private string _placeFor = string.Empty;
    private WeatherReport? _weather;
    private string _weatherError = string.Empty;

    private bool WeatherAvailable => _host.WeatherEnabled;

    private void InitWeather()
    {
        _weatherRefresh.Tick += async (_, _) => await RefreshWeatherAsync();
        _clockTick.Tick += (_, _) =>
        {
            if (_view == NotchView.Weather)
            {
                RenderWeather();
            }
        };
        Closed += (_, _) =>
        {
            _weatherRefresh.Stop();
            _clockTick.Stop();
        };
        RestartWeather();
    }

    /// <summary>Settings may have turned it on or off, or changed the city.</summary>
    private void RestartWeather()
    {
        if (!_host.WeatherEnabled && !_host.PrayerTimes)
        {
            _weatherRefresh.Stop();
            _clockTick.Stop();
            _weather = null;
            return;
        }

        _clockTick.Start();
        _weatherRefresh.Start();
        if (_placeFor != _host.WeatherCity.Trim())
        {
            _ = RefreshWeatherAsync();
        }
    }

    private async Task RefreshWeatherAsync()
    {
        var city = _host.WeatherCity.Trim();
        if ((!_host.WeatherEnabled && !_host.PrayerTimes) || city.Length == 0)
        {
            _weather = null;
            _weatherError = city.Length == 0 ? Tr.Text("Atur kotamu di Pengaturan → Notch.", "Set your city in Settings → Notch.") : string.Empty;
            return;
        }

        try
        {
            if (_place is null || _placeFor != city)
            {
                _place = await WeatherClient.FindPlaceAsync(city, Tr.IsEnglish ? "en" : "id");
                _placeFor = city;
                SchedulePrayer();   // prayer times follow the city
            }

            if (_place is null)
            {
                _weather = null;
                _weatherError = string.Format(Tr.Text("Kota \"{0}\" tidak ditemukan.", "Couldn't find \"{0}\"."), city);
            }
            else
            {
                _weather = await WeatherClient.ForecastAsync(_place);
                _weatherError = string.Empty;
            }
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or the service is away: keep the last report, try again at the next refresh.
            _weatherError = _weather is null ? Tr.Text("Cuaca belum bisa diambil (offline?).", "Couldn't get the weather (offline?).") : string.Empty;
            DiagnosticsLog.Error("Weather", exception.Message);
        }

        if (_view == NotchView.Weather)
        {
            RenderWeather();
        }
    }

    private void OpenWeather()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Weather;
        RenderWeather();
        _view = NotchView.Weather;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        if (_weather is null && _weatherError.Length == 0)
        {
            _ = RefreshWeatherAsync();
        }
    }

    private void RenderWeather()
    {
        var now = DateTimeOffset.Now;
        if (_weather is { } weather && _place is { } place)
        {
            var kind = WeatherClient.Kind(weather.Code);
            WeatherIcon.Data = kind switch
            {
                WeatherKind.Clear => weather.IsDay ? SunGlyph : MoonGlyph,
                WeatherKind.PartlyCloudy => weather.IsDay ? PartlyGlyph : Icons.CloudMoon,
                WeatherKind.Cloudy => CloudGlyph,
                WeatherKind.Fog => FogGlyph,
                WeatherKind.Drizzle => Icons.CloudDrizzle,
                WeatherKind.Rain => RainGlyph,
                WeatherKind.Snow => SnowGlyph,
                _ => StormGlyph
            };
            WeatherTemp.Text = $"{Math.Round(weather.Temperature):0}°";
            WeatherSummary.Text = WeatherClient.Describe(kind, Tr.Text);
            WeatherPlace.Text = place.Name;
            WeatherRange.Text = double.IsNaN(weather.High) ? string.Empty
                : $"↑ {Math.Round(weather.High):0}°  ↓ {Math.Round(weather.Low):0}°" +
                  (weather.RainChance is > 0 and var rain ? "  ·  " + string.Format(Tr.Text("hujan {0}%", "rain {0}%"), rain) : string.Empty);
            WeatherNow.Visibility = Visibility.Visible;
            WeatherMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            WeatherNow.Visibility = Visibility.Collapsed;
            WeatherMessage.Text = _weatherError.Length > 0 ? _weatherError : Tr.Text("Mengambil cuaca…", "Getting the weather…");
            WeatherMessage.Visibility = Visibility.Visible;
        }

        WorldClockRows.Children.Clear();
        WorldClockRows.Children.Add(ClockRow(Tr.Text("Di sini", "Here"), now.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture), string.Empty, first: true));
        foreach (var clock in WorldClock.ParseList(_host.WorldClocks))
        {
            var shift = clock.DayShift(now);
            var meta = string.Join("  ", new[] { clock.Offset(now), shift.Length > 0 ? (shift.StartsWith('+') ? Tr.Text("besok", "tomorrow") : Tr.Text("kemarin", "yesterday")) : string.Empty }
                .Where(part => part.Length > 0));
            WorldClockRows.Children.Add(ClockRow(clock.Label, clock.Time(now), meta, first: false));
        }

        if (PrayerAvailable && NextPrayer(now) is { } prayer)
        {
            WorldClockRows.Children.Add(ClockRow(prayer.Name(Tr.Text), prayer.At.ToString("H:mm", CultureInfo.InvariantCulture), Tr.Text("sholat berikutnya", "next prayer"), first: false));
        }

        RenderPageDots();
    }

    private FrameworkElement ClockRow(string label, string time, string meta, bool first)
    {
        var grid = new Grid { Height = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = first ? TimerQuietBrush : TimerFocusBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { Text = meta, FontSize = 11.5, Foreground = TimerQuietBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);
        var clock = new TextBlock
        {
            Text = time,
            Style = (Style)FindResource("NotchDigits"),
            FontSize = 15,
            Foreground = first ? TimerQuietBrush : TimerFocusBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(clock, 2);
        grid.Children.Add(clock);
        return new Border
        {
            BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Child = grid
        };
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewWeather()
    {
        await Task.Delay(400);
        _place = new GeoPlace("Jakarta", "Indonesia", -6.2, 106.8, "Asia/Jakarta");
        _placeFor = "Jakarta";
        _weather = new WeatherReport(31.4, 2, true, 33.1, 25.0, 70);
        OpenWeather();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-weather.png"));
        }
    }
}
