using Cs2DropChecker.Core.Models;

namespace Cs2DropChecker.Core.Weekly;

public readonly record struct WeeklyResetTime(DayOfWeek Day, int HourUtc, int MinuteUtc);

public static class Cs2Week
{
    /// <summary>
    /// When CS2's weekly drop limit resets. Valve doesn't publish this; community trackers (p337.info, monitoringcs2.com,
    /// daddyskins.com, checked 2026-10-25) agree on Wednesday 01:00 UTC, which does not move with daylight saving time.
    /// </summary>
    public static readonly WeeklyResetTime Reset = new(DayOfWeek.Wednesday, 1, 0);

    /// <summary>The most recent reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset Start(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var back = ((int)utc.DayOfWeek - (int)Reset.Day + 7) % 7;
        var candidate = new DateTimeOffset(utc.Year, utc.Month, utc.Day, Reset.HourUtc, Reset.MinuteUtc, 0, TimeSpan.Zero).AddDays(-back);
        return candidate > utc ? candidate.AddDays(-7) : candidate;
    }

    public static DateTimeOffset NextReset(DateTimeOffset now) => Start(now).AddDays(7);
}

/// <summary>Whether this CS2 week's drop has arrived and how many weeks in a row had one.</summary>
/// <param name="Streak">Consecutive CS2 weeks with a weekly drop, ending with this week or, while this week has none yet, the last one.</param>
/// <param name="AtRisk">This week has no drop yet but the streak is still alive.</param>
public readonly record struct WeeklyStatus(bool ThisWeek, int Streak, bool AtRisk, DateTimeOffset NextReset);

public static class WeeklyStreak
{
    public static WeeklyStatus Compute(IEnumerable<DropRecord> drops, DateTimeOffset now)
    {
        var weeks = drops.Where(d => d.Kind == ArrivalKind.WeeklyDrop && d.SeenAt <= now)
            .Select(d => Cs2Week.Start(d.SeenAt))
            .ToHashSet();
        var start = Cs2Week.Start(now);
        var thisWeek = weeks.Contains(start);
        var streak = 0;
        for (var w = thisWeek ? start : start.AddDays(-7); weeks.Contains(w); w = w.AddDays(-7)) streak++;
        return new WeeklyStatus(thisWeek, streak, !thisWeek && streak > 0, start.AddDays(7));
    }
}

public static class WeeklyReminder
{
    public static bool HasWeeklyDrop(IEnumerable<DropRecord> drops, DateTimeOffset now)
    {
        var start = Cs2Week.Start(now);
        return drops.Any(d => d.Kind == ArrivalKind.WeeklyDrop && d.SeenAt >= start);
    }

    /// <summary>The first time inside the CS2 week starting at <paramref name="weekStart"/> that falls on the chosen local day and time.</summary>
    public static DateTimeOffset ReminderTime(DateTimeOffset weekStart, DayOfWeek day, TimeSpan localTime, TimeZoneInfo zone)
    {
        var localStart = TimeZoneInfo.ConvertTime(weekStart, zone);
        for (var i = 0; i <= 7; i++)
        {
            var date = localStart.Date.AddDays(i);
            if (date.DayOfWeek != day) continue;
            var local = date + localTime;
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at >= weekStart && at < weekStart.AddDays(7)) return at;
        }
        return weekStart.AddDays(7);
    }

    /// <summary>True once per CS2 week, from the chosen time until the next reset, while no weekly drop has arrived.</summary>
    public static bool IsDue(DateTimeOffset now, bool hasWeeklyDrop, DateTimeOffset? lastShownWeek, DayOfWeek day, TimeSpan localTime, TimeZoneInfo zone)
    {
        if (hasWeeklyDrop) return false;
        var start = Cs2Week.Start(now);
        if (lastShownWeek is { } shown && shown >= start) return false;
        return now >= ReminderTime(start, day, localTime, zone);
    }
}
