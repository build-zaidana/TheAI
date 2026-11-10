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
            WeatherNow.Visibility = Visibility.Visible;
            WeatherMessage.Visibility = Visibility.Collapsed;
            WeatherLine.Visibility = weather.Hourly is null ? Visibility.Hidden : Visibility.Visible;
            if (weather.Hourly is { } hours)
            {
                WeatherLine.Mark = Math.Min(LocalHour(place, now), hours.Count - 1);
                WeatherLine.Show(hours);
            }
        }
        else
        {
            WeatherPlace.Text = Tr.Text("Cuaca", "Weather");
            WeatherSummary.Text = string.Empty;
            WeatherLine.Visibility = Visibility.Hidden;
            WeatherNow.Visibility = Visibility.Collapsed;
            WeatherMessage.Text = _weatherError.Length > 0 ? _weatherError : Tr.Text("Mengambil cuaca…", "Getting the weather…");
            WeatherMessage.Visibility = Visibility.Visible;
        }

        // The footer holds five: the day's numbers first, then the next prayer and the clocks you set.
        var cells = new List<(string Label, string Value)>();
        if (_weather is { } today && !double.IsNaN(today.High))
        {
            cells.Add((Tr.Text("Tertinggi", "High"), $"{Math.Round(today.High):0}°"));
            cells.Add((Tr.Text("Terendah", "Low"), $"{Math.Round(today.Low):0}°"));
            if (today.RainChance is { } rain)
            {
                cells.Add((Tr.Text("Hujan", "Rain"), $"{rain}%"));
            }
        }

        if (PrayerAvailable && NextPrayer(now) is { } prayer)
        {
            cells.Add((prayer.Name(Tr.Text), prayer.At.ToString("H:mm", CultureInfo.InvariantCulture)));
        }

        foreach (var clock in WorldClock.ParseList(_host.WorldClocks))
        {
            var shift = clock.DayShift(now);
            cells.Add((clock.Label, clock.Time(now) + (shift.StartsWith('+') ? " +1" : shift.Length > 0 ? " −1" : string.Empty)));
        }

        cells.Add((Tr.Text("Di sini", "Here"), now.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture)));
        WorldClockRows.Children.Clear();
        var shown = cells.Take(5).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            WorldClockRows.Children.Add(FootCell(shown[i].Label, shown[i].Value, last: i == shown.Count - 1 && shown.Count > 1));
        }

        RenderPageDots();
    }

    private static int LocalHour(GeoPlace place, DateTimeOffset now)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(place.TimeZone)).Hour;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return now.ToLocalTime().Hour;
        }
    }

    /// <summary>One label-over-value item of a page's footer (board 31).</summary>
    private FrameworkElement FootCell(string label, string value, bool last)
    {
        var align = last ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var cell = new StackPanel();
        cell.Children.Add(new TextBlock { Style = (Style)FindResource("IslandFootLabel"), Foreground = IslandTokens.Text3, Text = label, HorizontalAlignment = align });
        cell.Children.Add(new TextBlock { Style = (Style)FindResource("IslandFootValue"), Foreground = IslandTokens.Text2, Text = value, HorizontalAlignment = align });
        return cell;
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewWeather()
    {
        await Task.Delay(400);
        _place = new GeoPlace("Jakarta", "Indonesia", -6.2, 106.8, "Asia/Jakarta");
        _placeFor = "Jakarta";
        _weather = new WeatherReport(31.4, 2, true, 33.1, 25.0, 70,
            [26, 25.6, 25.2, 25, 25, 25.3, 26, 27.2, 28.6, 30, 31.2, 32.1, 32.8, 33.1, 33, 32.4, 31.6, 30.4, 29.2, 28.3, 27.6, 27, 26.6, 26.2]);
        OpenWeather();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-weather.png"));
        }
    }
}
