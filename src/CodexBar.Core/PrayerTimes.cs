namespace CodexBar.Core;

/// <summary>One of the five daily prayers and when it begins.</summary>
public sealed record Prayer(string NameId, string NameEn, DateTimeOffset At)
{
    public string Name(Func<string, string, string> text) => text(NameId, NameEn);
}

/// <summary>
/// The five prayer times worked out on the PC from a place's coordinates (no network): the sun's
/// position for the day, with Indonesia's Ministry of Religious Affairs (Kemenag) angles — Fajr at
/// 20° and Isha at 18° below the horizon, Asr when a shadow is its object's length plus noon's
/// shadow (Shafi'i) — and two minutes of ihtiyat added, as Kemenag's tables do.
/// </summary>
public static class PrayerTimes
{
    private const double FajrAngle = 20;
    private const double IshaAngle = 18;
    private static readonly TimeSpan Ihtiyat = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<Prayer> For(DateOnly day, double latitude, double longitude, TimeSpan utcOffset)
    {
        var (declination, equation) = Sun(day);
        var noon = 12 + utcOffset.TotalHours - (longitude / 15) - equation;   // local clock hours

        double HourAngle(double altitude)
        {
            var lat = Rad(latitude);
            var dec = Rad(declination);
            var cos = (Math.Sin(Rad(altitude)) - (Math.Sin(lat) * Math.Sin(dec))) / (Math.Cos(lat) * Math.Cos(dec));
            return Deg(Math.Acos(Math.Clamp(cos, -1, 1))) / 15;
        }

        var asrAltitude = Deg(Math.Atan(1 / (1 + Math.Tan(Rad(Math.Abs(latitude - declination))))));
        var sunset = noon + HourAngle(-0.8333);
        double[] hours =
        [
            noon - HourAngle(-FajrAngle),
            noon,
            noon + HourAngle(asrAltitude),
            sunset,
            noon + HourAngle(-IshaAngle),
        ];
        (string Id, string En)[] names = [("Subuh", "Fajr"), ("Dzuhur", "Dhuhr"), ("Ashar", "Asr"), ("Maghrib", "Maghrib"), ("Isya", "Isha")];
        var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), utcOffset);
        return names.Select((name, i) =>
        {
            var at = midnight.AddHours(hours[i]) + Ihtiyat;
            at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);   // whole minutes, like the printed tables
            return new Prayer(name.Id, name.En, at);
        }).ToList();
    }

    /// <summary>The next prayer from now (tomorrow's Fajr after Isha).</summary>
    public static Prayer Next(DateTimeOffset now, double latitude, double longitude)
    {
        var today = For(DateOnly.FromDateTime(now.DateTime), latitude, longitude, now.Offset);
        return today.FirstOrDefault(prayer => prayer.At > now)
            ?? For(DateOnly.FromDateTime(now.DateTime).AddDays(1), latitude, longitude, now.Offset)[0];
    }

    // The sun's declination (degrees) and the equation of time (hours) at noon UTC of the day.
    private static (double Declination, double Equation) Sun(DateOnly day)
    {
        var julian = day.ToDateTime(new TimeOnly(12, 0)).ToOADate() + 2415018.5;
        var d = julian - 2451545.0;
        var g = Normalize(357.529 + (0.98560028 * d));
        var q = Normalize(280.459 + (0.98564736 * d));
        var l = Normalize(q + (1.915 * Math.Sin(Rad(g))) + (0.020 * Math.Sin(Rad(2 * g))));
        var e = 23.439 - (0.00000036 * d);
        var ra = Deg(Math.Atan2(Math.Cos(Rad(e)) * Math.Sin(Rad(l)), Math.Cos(Rad(l)))) / 15;
        var declination = Deg(Math.Asin(Math.Sin(Rad(e)) * Math.Sin(Rad(l))));
        var equation = (q / 15) - NormalizeHours(ra);
        if (equation > 12)
        {
            equation -= 24;
        }
        else if (equation < -12)
        {
            equation += 24;
        }

        return (declination, equation);
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static double Deg(double radians) => radians * 180 / Math.PI;

    private static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;

    private static double NormalizeHours(double hours) => ((hours % 24) + 24) % 24;
}
