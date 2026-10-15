namespace FolderSentinel.Tests;

public class ScheduleTests
{
    private static NotificationSchedule Schedule(ScheduleFrequency frequency, string time = "09:00") => new() { Frequency = frequency, Time = time, TimeZoneId = "UTC" };
    [Fact] public void DailyIsStrictlyAfterPreviousOccurrence()
    {
        var schedule = Schedule(ScheduleFrequency.Daily);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T09:00Z"), ScheduleCalculator.Next(schedule, DateTimeOffset.Parse("2026-10-15T09:00Z")));
    }
    [Fact] public void WeeklyChoosesNextSelectedDay()
    {
        var schedule = Schedule(ScheduleFrequency.Weekly); schedule.Days = [DayOfWeek.Monday, DayOfWeek.Friday];
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T09:00Z"), ScheduleCalculator.Next(schedule, DateTimeOffset.Parse("2026-10-15T18:00Z")));
    }
    [Theory] [InlineData(2027, 28)] [InlineData(2028, 29)]
    public void MissingMonthlyDayClampsToMonthEnd(int year, int day)
    {
        var schedule = Schedule(ScheduleFrequency.Monthly); schedule.MonthDay = 31;
        Assert.Equal(new DateTimeOffset(year, 2, day, 9, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(schedule, new DateTimeOffset(year, 2, 1, 0, 0, 0, TimeSpan.Zero)));
    }
    [Fact] public void ExplicitMonthEndWorksAcrossYears()
    {
        var schedule = Schedule(ScheduleFrequency.Monthly); schedule.LastDay = true;
        Assert.Equal(DateTimeOffset.Parse("2027-01-31T09:00Z"), ScheduleCalculator.Next(schedule, DateTimeOffset.Parse("2026-12-31T09:00Z")));
    }
    [Theory] [InlineData("25:00")] [InlineData("9:0")] [InlineData("文字")]
    public void InvalidTimeIsRejected(string time) => Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(Schedule(ScheduleFrequency.Daily, time)));
    [Fact] public void EmptyWeekdaysAreRejected()
    {
        var schedule = Schedule(ScheduleFrequency.Weekly); schedule.Days = []; Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(schedule));
    }
    [Fact] public void DstGapMovesToFirstValidMinute()
    {
        var schedule = Schedule(ScheduleFrequency.Daily, "02:30"); schedule.TimeZoneId = "Eastern Standard Time";
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00Z"), ScheduleCalculator.Next(schedule, DateTimeOffset.Parse("2026-03-08T00:00Z")));
    }
    [Fact] public void DstOverlapDoesNotRunTwice()
    {
        var schedule = Schedule(ScheduleFrequency.Daily, "01:30"); schedule.TimeZoneId = "Eastern Standard Time";
        var first = ScheduleCalculator.Next(schedule, DateTimeOffset.Parse("2026-11-01T00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30Z"), first);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T06:30Z"), ScheduleCalculator.Next(schedule, first));
    }
}
