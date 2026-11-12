using System.Globalization;

namespace Vera.Core;

/// <summary>Русские форматы дат и времени.</summary>
public static class RuText
{
    private static readonly string[] Weekdays = { "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота" };
    private static readonly string[] Months = { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" };

    /// <summary>«12 ноября 2026, 15:04» (в локальном времени)</summary>
    public static string FormatDateTime(DateTimeOffset t)
    {
        var d = t.ToLocalTime();
        return $"{d.Day} {Months[d.Month - 1]} {d.Year}, {d:HH\\:mm}";
    }

    /// <summary>«сегодня 15:04», «завтра 09:00», «пт, 14 нояб. 18:00»</summary>
    public static string WhenShort(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var d = t.ToLocalTime();
        var n = (now ?? DateTimeOffset.Now).ToLocalTime();
        var time = d.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (d.Date == n.Date) return "сегодня " + time;
        if (d.Date == n.Date.AddDays(1)) return "завтра " + time;
        return d.ToString("ddd, d MMM", new CultureInfo("ru-RU")) + " " + time;
    }

    public static string LocalIso(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public static string Plural(long n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a > 10 && a < 20) return many;
        if (b > 1 && b < 5) return few;
        return b == 1 ? one : many;
    }

    /// <summary>«через 2 ч 5 мин» / «5 мин назад»</summary>
    public static string Relative(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var diff = (long)Math.Round((t - (now ?? DateTimeOffset.Now)).TotalMinutes);
        var past = diff < 0;
        diff = Math.Abs(diff);
        string s;
        if (diff < 1) s = "меньше минуты";
        else if (diff < 60) s = $"{diff} мин";
        else if (diff < 60 * 24)
        {
            var h = diff / 60; var m = diff % 60;
            s = $"{h} ч" + (m > 0 ? $" {m} мин" : "");
        }
        else
        {
            var days = diff / 1440; var h = diff % 1440 / 60;
            s = $"{days} {Plural(days, "день", "дня", "дней")}" + (h > 0 ? $" {h} ч" : "");
        }
        return past ? s + " назад" : "через " + s;
    }

    /// <summary>Строка контекста времени для модели (добавляется к каждому сообщению пользователя).</summary>
    public static string TimeContext(DateTimeOffset now)
    {
        var d = now.ToLocalTime();
        var off = d.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        var tz = TimeZoneInfo.Local.Id;
        return $"Сейчас: {Weekdays[(int)d.DayOfWeek]}, {FormatDateTime(now)} (локальное время {LocalIso(now)}, {tz} UTC{sign}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00})";
    }

    public static string Truncate(string? s, int n)
    {
        s ??= "";
        return s.Length > n ? s[..n] + $"… [обрезано, всего {s.Length} симв.]" : s;
    }
}
