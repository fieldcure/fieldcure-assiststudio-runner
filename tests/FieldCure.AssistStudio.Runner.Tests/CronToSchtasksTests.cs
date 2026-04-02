using FieldCure.AssistStudio.Runner.Scheduling;

namespace FieldCure.AssistStudio.Runner.Tests;

[TestClass]
public class CronToSchtasksTests
{
    [TestMethod]
    public void MinuteInterval_30Min()
    {
        var trigger = CronToSchtasks.Convert("*/30 * * * *");
        Assert.AreEqual(ScheduleType.Minute, trigger.Type);
        Assert.AreEqual(30, trigger.Interval);
        Assert.AreEqual("/SC MINUTE /MO 30", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void MinuteInterval_5Min()
    {
        var trigger = CronToSchtasks.Convert("*/5 * * * *");
        Assert.AreEqual(ScheduleType.Minute, trigger.Type);
        Assert.AreEqual(5, trigger.Interval);
    }

    [TestMethod]
    public void HourlyInterval_2Hours()
    {
        var trigger = CronToSchtasks.Convert("0 */2 * * *");
        Assert.AreEqual(ScheduleType.Hourly, trigger.Type);
        Assert.AreEqual(2, trigger.Interval);
        Assert.AreEqual("/SC HOURLY /MO 2 /ST 00:00", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void Daily_9AM()
    {
        var trigger = CronToSchtasks.Convert("0 9 * * *");
        Assert.AreEqual(ScheduleType.Daily, trigger.Type);
        Assert.AreEqual("09:00", trigger.StartTime);
        Assert.AreEqual("/SC DAILY /ST 09:00", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void Weekdays_9AM()
    {
        var trigger = CronToSchtasks.Convert("0 9 * * 1-5");
        Assert.AreEqual(ScheduleType.Weekly, trigger.Type);
        Assert.AreEqual("09:00", trigger.StartTime);
        Assert.IsNotNull(trigger.Days);
        CollectionAssert.AreEqual(
            new[] { "MON", "TUE", "WED", "THU", "FRI" },
            trigger.Days);
        Assert.AreEqual("/SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 09:00", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void WeeklyMonday_9AM()
    {
        var trigger = CronToSchtasks.Convert("0 9 * * 1");
        Assert.AreEqual(ScheduleType.Weekly, trigger.Type);
        Assert.IsNotNull(trigger.Days);
        CollectionAssert.AreEqual(new[] { "MON" }, trigger.Days);
    }

    [TestMethod]
    public void Monthly_FirstDay_9AM()
    {
        var trigger = CronToSchtasks.Convert("0 9 1 * *");
        Assert.AreEqual(ScheduleType.Monthly, trigger.Type);
        Assert.AreEqual("09:00", trigger.StartTime);
        Assert.AreEqual(1, trigger.DayOfMonth);
        Assert.AreEqual("/SC MONTHLY /D 1 /ST 09:00", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void Monthly_15th_6PM()
    {
        var trigger = CronToSchtasks.Convert("0 18 15 * *");
        Assert.AreEqual(ScheduleType.Monthly, trigger.Type);
        Assert.AreEqual("18:00", trigger.StartTime);
        Assert.AreEqual(15, trigger.DayOfMonth);
    }

    [TestMethod]
    public void Daily_WithMinutes()
    {
        var trigger = CronToSchtasks.Convert("30 14 * * *");
        Assert.AreEqual(ScheduleType.Daily, trigger.Type);
        Assert.AreEqual("14:30", trigger.StartTime);
    }

    [TestMethod]
    public void BareStar_MinuteField_TreatedAsEveryMinute()
    {
        var trigger = CronToSchtasks.Convert("* * * * *");
        Assert.AreEqual(ScheduleType.Minute, trigger.Type);
        Assert.AreEqual(1, trigger.Interval);
        Assert.AreEqual("/SC MINUTE /MO 1", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void BareStar_MinuteField_EquivalentToSlash1()
    {
        var star = CronToSchtasks.Convert("* * * * *");
        var slash1 = CronToSchtasks.Convert("*/1 * * * *");

        Assert.AreEqual(slash1.Type, star.Type);
        Assert.AreEqual(slash1.Interval, star.Interval);
        Assert.AreEqual(slash1.ToSchtasksArgs(), star.ToSchtasksArgs());
    }

    [TestMethod]
    public void BareStar_HourField_TreatedAsHourly()
    {
        var trigger = CronToSchtasks.Convert("0 * * * *");
        Assert.AreEqual(ScheduleType.Hourly, trigger.Type);
        Assert.AreEqual(1, trigger.Interval);
        Assert.AreEqual("/SC HOURLY /MO 1 /ST 00:00", trigger.ToSchtasksArgs());
    }

    [TestMethod]
    public void BareStar_HourField_EquivalentToSlash1()
    {
        var star = CronToSchtasks.Convert("0 * * * *");
        var slash1 = CronToSchtasks.Convert("0 */1 * * *");

        Assert.AreEqual(slash1.Type, star.Type);
        Assert.AreEqual(slash1.Interval, star.Interval);
        Assert.AreEqual(slash1.ToSchtasksArgs(), star.ToSchtasksArgs());
    }

    [TestMethod]
    public void ComplexPattern_Throws()
    {
        Assert.ThrowsExactly<UnsupportedScheduleException>(
            () => CronToSchtasks.Convert("0 9 1,15 * *")); // Multiple days of month
    }

    [TestMethod]
    public void MonthSpecific_Throws()
    {
        Assert.ThrowsExactly<UnsupportedScheduleException>(
            () => CronToSchtasks.Convert("0 9 * 3 *")); // March only
    }

    [TestMethod]
    public void InvalidFieldCount_Throws()
    {
        Assert.ThrowsExactly<UnsupportedScheduleException>(
            () => CronToSchtasks.Convert("0 9 *"));
    }

    [TestMethod]
    public void Weekend_Days()
    {
        var trigger = CronToSchtasks.Convert("0 10 * * 0,6");
        Assert.AreEqual(ScheduleType.Weekly, trigger.Type);
        Assert.IsNotNull(trigger.Days);
        CollectionAssert.AreEqual(new[] { "SUN", "SAT" }, trigger.Days);
    }
}
