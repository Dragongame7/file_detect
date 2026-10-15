using System.Globalization;

namespace FolderSentinel.Core;

public static class ScheduleCalculator
{
    public static void Validate(NotificationSchedule schedule)
    {
        if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("時刻は 09:00 のように24時間形式で入力してください。");
        if (schedule.Frequency == ScheduleFrequency.Weekly && (schedule.Days.Count == 0 || schedule.Days.Any(x => !Enum.IsDefined(x))))
            throw new ArgumentException("通知する曜日を1つ以上選択してください。");
        if (schedule.MonthDay is < 1 or > 31) throw new ArgumentException("日にちは1〜31を指定してください。");
        if (!Enum.IsDefined(schedule.Frequency)) throw new ArgumentException("通知周期が無効です。");
        _ = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
    }

    public static DateTimeOffset Next(NotificationSchedule schedule, DateTimeOffset afterUtc)
    {
        Validate(schedule);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var start = TimeZoneInfo.ConvertTime(afterUtc, zone).Date;
        var time = TimeOnly.ParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture);
        for (int i = 0; i < 800; i++)
        {
            var day = start.AddDays(i);
            bool matches = schedule.Frequency switch
            {
                ScheduleFrequency.Daily => true,
                ScheduleFrequency.Weekly => schedule.Days.Contains(day.DayOfWeek),
                _ => day.Day == (schedule.LastDay ? DateTime.DaysInMonth(day.Year, day.Month) : Math.Min(schedule.MonthDay, DateTime.DaysInMonth(day.Year, day.Month)))
            };
            if (!matches) continue;
            var wallTime = DateTime.SpecifyKind(day.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(wallTime)) wallTime = wallTime.AddMinutes(1);
            var offset = zone.IsAmbiguousTime(wallTime) ? zone.GetAmbiguousTimeOffsets(wallTime).Max() : zone.GetUtcOffset(wallTime);
            var candidate = new DateTimeOffset(wallTime, offset).ToUniversalTime();
            if (candidate > afterUtc) return candidate;
        }
        throw new InvalidOperationException("次回の通知日時を計算できませんでした。");
    }
}
