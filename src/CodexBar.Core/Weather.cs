using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>A city found by name: where it is and its time zone.</summary>
public sealed record GeoPlace(string Name, string Country, double Latitude, double Longitude, string TimeZone);

/// <summary>Now and today for one place.</summary>
public sealed record WeatherReport(double Temperature, int Code, bool IsDay, double High, double Low, int? RainChance);

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

/// <summary>
/// Weather from Open-Meteo (no API key, no account): the city is looked up once by name, then
/// the forecast is asked for by coordinates. Only the city name and its coordinates leave the PC.
/// </summary>
public static class WeatherClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TheAI-Weather");
        return client;
    }

    public static async Task<GeoPlace?> FindPlaceAsync(string city, string language, CancellationToken cancellationToken = default)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&language={Uri.EscapeDataString(language)}&name={Uri.EscapeDataString(city.Trim())}";
        return ParsePlace(await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<WeatherReport?> ForecastAsync(GeoPlace place, CancellationToken cancellationToken = default)
    {
        var url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
            "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=1&timezone=auto",
            place.Latitude, place.Longitude);
        return ParseForecast(await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public static GeoPlace? ParsePlace(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return null;
        }

        var first = results[0];
        return new GeoPlace(
            Text(first, "name") ?? string.Empty,
            Text(first, "country") ?? string.Empty,
            first.GetProperty("latitude").GetDouble(),
            first.GetProperty("longitude").GetDouble(),
            Text(first, "timezone") ?? "UTC");
    }

    public static WeatherReport? ParseForecast(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("current", out var current) || !root.TryGetProperty("daily", out var daily))
        {
            return null;
        }

        static double First(JsonElement daily, string name) =>
            daily.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 && array[0].ValueKind == JsonValueKind.Number
                ? array[0].GetDouble()
                : double.NaN;

        var rain = First(daily, "precipitation_probability_max");
        return new WeatherReport(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            !current.TryGetProperty("is_day", out var day) || day.GetInt32() == 1,
            First(daily, "temperature_2m_max"),
            First(daily, "temperature_2m_min"),
            double.IsNaN(rain) ? null : (int)Math.Round(rain));
    }

    /// <summary>WMO weather codes (what Open-Meteo returns) in a few kinds.</summary>
    public static WeatherKind Kind(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 57 => WeatherKind.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => WeatherKind.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherKind.Snow,
        >= 95 => WeatherKind.Storm,
        _ => WeatherKind.Cloudy
    };

    public static string Describe(WeatherKind kind, Func<string, string, string> text) => kind switch
    {
        WeatherKind.Clear => text("Cerah", "Clear"),
        WeatherKind.PartlyCloudy => text("Cerah berawan", "Partly cloudy"),
        WeatherKind.Cloudy => text("Berawan", "Cloudy"),
        WeatherKind.Fog => text("Berkabut", "Foggy"),
        WeatherKind.Drizzle => text("Gerimis", "Drizzle"),
        WeatherKind.Rain => text("Hujan", "Rain"),
        WeatherKind.Snow => text("Salju", "Snow"),
        _ => text("Badai petir", "Thunderstorm")
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>One extra clock: a label and its zone.</summary>
public sealed record WorldClock(string Label, TimeZoneInfo Zone)
{
    /// <summary>
    /// "Tokyo=Asia/Tokyo, America/New_York, London=GMT Standard Time": IANA or Windows zone ids,
    /// with an optional label (else the city part of the IANA id). Unknown zones are skipped.
    /// </summary>
    public static IReadOnlyList<WorldClock> ParseList(string text)
    {
        var clocks = new List<WorldClock>();
        foreach (var part in text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var label = (string?)null;
            var id = part;
            if (part.IndexOf('=') is var equals and > 0)
            {
                label = part[..equals].Trim();
                id = part[(equals + 1)..].Trim();
            }

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                label ??= id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ') : zone.StandardName;
                clocks.Add(new WorldClock(label, zone));
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // A typo: that clock is left out, the rest still show.
            }

            if (clocks.Count == 4)
            {
                break;
            }
        }

        return clocks;
    }

    /// <summary>"Tue", "+1", "-1" or "" for the same day as here.</summary>
    public string DayShift(DateTimeOffset now)
    {
        var there = TimeZoneInfo.ConvertTime(now, Zone).Date;
        var here = now.ToLocalTime().Date;
        var days = (there - here).Days;
        return days == 0 ? string.Empty : days > 0 ? $"+{days}" : days.ToString(CultureInfo.InvariantCulture);
    }

    public string Time(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Zone).ToString("H:mm", CultureInfo.InvariantCulture);

    /// <summary>"+2h", "-5h30m": how far ahead of here.</summary>
    public string Offset(DateTimeOffset now)
    {
        var difference = Zone.GetUtcOffset(now) - TimeZoneInfo.Local.GetUtcOffset(now);
        if (difference == TimeSpan.Zero)
        {
            return string.Empty;
        }

        var sign = difference < TimeSpan.Zero ? "-" : "+";
        difference = difference.Duration();
        return difference.Minutes == 0 ? $"{sign}{(int)difference.TotalHours}h" : $"{sign}{(int)difference.TotalHours}h{difference.Minutes}m";
    }
}
