using System;
using System.Globalization;
using System.Text;
using Jorjin.Streaming;

/// <summary>
/// Writes meeting action items with dates as an iCalendar (.ics) file, which
/// Google Calendar, Outlook and phone calendars can import as all-day events.
/// </summary>
public static class CalendarFile
{
    public static string Build(CollaborationCalendarEvent[] events)
    {
        var ics = new StringBuilder();
        Line(ics, "BEGIN:VCALENDAR");
        Line(ics, "VERSION:2.0");
        Line(ics, "PRODID:-//Jorjin//Meeting Action Items//ZH");
        Line(ics, "CALSCALE:GREGORIAN");
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (CollaborationCalendarEvent item in events ?? Array.Empty<CollaborationCalendarEvent>())
        {
            if (item == null || !DateTime.TryParseExact(item.date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime day))
                continue;
            Line(ics, "BEGIN:VEVENT");
            Line(ics, "UID:" + Escape(item.id) + "@jorjin-meeting");
            Line(ics, "DTSTAMP:" + stamp);
            Line(ics, "DTSTART;VALUE=DATE:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(ics, "DTEND;VALUE=DATE:" + day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(ics, "SUMMARY:" + Escape(item.title + "（" + item.owner + "）"));
            Line(ics, "DESCRIPTION:" + Escape("負責人：" + item.owner + "\n期限：" + item.deadline));
            Line(ics, "END:VEVENT");
        }
        Line(ics, "END:VCALENDAR");
        return ics.ToString();
    }

    private static void Line(StringBuilder ics, string text) => ics.Append(text).Append("\r\n");

    private static string Escape(string value) => (value ?? "")
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r", "").Replace("\n", "\\n");
}
