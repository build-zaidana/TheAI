using System.Globalization;
using System.Net.Http;
using CodexBar.Core;

namespace CodexBar.App.Hud;

/// <summary>
/// Today's events for the notch, from the source the person picked: Outlook (read locally through
/// its COM interface, recurring meetings expanded by Outlook itself) or an iCal link they pasted
/// (kept in Windows Credential Manager, not in the settings file). Nothing is read while it is off.
/// </summary>
internal static class CalendarReader
{
    public const string IcsCredential = "CodexBar/CalendarIcs";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<IReadOnlyList<CalendarEvent>> TodayAsync(string source)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            switch (source)
            {
                case "outlook":
                    return await RunSta(() => ReadOutlook(today));
                case "ics" when CredentialStore.TryRead(IcsCredential) is { Length: > 0 } url:
                    var text = await Http.GetStringAsync(url.Replace("webcal://", "https://", StringComparison.OrdinalIgnoreCase));
                    return IcsCalendar.EventsOn(text, today, TimeZoneInfo.Local);
            }
        }
        catch (Exception exception)
        {
            // Never the link itself in the log: it is a secret address.
            DiagnosticsLog.Error("Calendar", source + ": " + exception.GetType().Name + " " + exception.Message.Replace(CredentialStore.TryRead(IcsCredential) ?? "\u0000", "<link>"));
        }

        return [];
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        }) { IsBackground = true, Name = "Outlook calendar" };
        thread.SetApartmentState(ApartmentState.STA);   // Outlook's object model is apartment-threaded
        thread.Start();
        return done.Task;
    }

    private static IReadOnlyList<CalendarEvent> ReadOutlook(DateOnly day)
    {
        var type = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("Outlook is not installed");
        dynamic outlook = Activator.CreateInstance(type)!;
        dynamic calendar = outlook.GetNamespace("MAPI").GetDefaultFolder(9 /* olFolderCalendar */);
        dynamic items = calendar.Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        var from = day.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(1);
        // Outlook's filter takes dates written the way the system writes them.
        string Stamp(DateTime when) => when.ToString("g", CultureInfo.CurrentCulture);
        dynamic found = items.Restrict($"[Start] < '{Stamp(to)}' AND [End] > '{Stamp(from)}'");
        var events = new List<CalendarEvent>();
        foreach (dynamic item in found)
        {
            if (events.Count >= 30)
            {
                break;
            }

            DateTime start = item.Start;
            DateTime end = item.End;
            if (DateOnly.FromDateTime(start) != day && !(bool)item.AllDayEvent)
            {
                continue;   // started on an earlier day
            }

            string subject = item.Subject ?? string.Empty;
            string location = item.Location ?? string.Empty;
            string body = string.Empty;
            try
            {
                body = item.Body ?? string.Empty;
            }
            catch
            {
                // Some items refuse their body (protected); the location may still hold the link.
            }

            events.Add(new CalendarEvent(subject, new DateTimeOffset(start), new DateTimeOffset(end), (bool)item.AllDayEvent,
                MeetingLinks.Find(location, body.Length > 20000 ? body[..20000] : body)));
        }

        return events.OrderBy(item => item.AllDay ? 0 : 1).ThenBy(item => item.Start).ToList();
    }
}
