namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Converts cron expressions to schtasks trigger parameters.
/// Supports: MINUTE, HOURLY, DAILY, WEEKLY, MONTHLY (simple patterns).
/// </summary>
public static class CronToSchtasks
{
    /// <summary>Maps cron day-of-week numbers (0-7) to schtasks day abbreviations.</summary>
    static readonly Dictionary<string, string> DayMap = new()
    {
        ["0"] = "SUN", ["1"] = "MON", ["2"] = "TUE", ["3"] = "WED",
        ["4"] = "THU", ["5"] = "FRI", ["6"] = "SAT", ["7"] = "SUN",
    };

    /// <summary>
    /// Parses a 5-field cron expression and returns schtasks trigger info.
    /// </summary>
    /// <exception cref="UnsupportedScheduleException">
    /// Thrown when the cron expression cannot be mapped to schtasks.
    /// </exception>
    public static SchtasksTrigger Convert(string cron)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new UnsupportedScheduleException(cron,
                "Expected 5 fields: minute hour day-of-month month day-of-week");

        // Normalize: bare "*" is semantically identical to "*/1"
        for (var i = 0; i < parts.Length; i++)
            if (parts[i] == "*") parts[i] = "*/1";

        var (minute, hour, dom, month, dow) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        // Validate month field — only * is supported in v2.0
        if (month != "*/1")
            throw new UnsupportedScheduleException(cron,
                "Month-specific schedules are not supported. Use '*' for month.");

        // Pattern: */N * * * * → MINUTE
        if (minute.StartsWith("*/") && hour == "*/1" && dom == "*/1" && dow == "*/1")
        {
            if (!int.TryParse(minute[2..], out var interval) || interval < 1)
                throw new UnsupportedScheduleException(cron, "Invalid minute interval.");

            return new SchtasksTrigger(ScheduleType.Minute, interval, "00:00",
                Description: $"Every {interval} minute(s)");
        }

        // Pattern: 0 */N * * * → HOURLY
        if (minute == "0" && hour.StartsWith("*/") && dom == "*/1" && dow == "*/1")
        {
            if (!int.TryParse(hour[2..], out var interval) || interval < 1)
                throw new UnsupportedScheduleException(cron, "Invalid hour interval.");

            return new SchtasksTrigger(ScheduleType.Hourly, interval, "00:00",
                Description: $"Every {interval} hour(s)");
        }

        // From here, minute and hour must be fixed values
        if (!int.TryParse(minute, out var min) || min < 0 || min > 59)
            throw new UnsupportedScheduleException(cron, "Minute must be a fixed value (0-59) for this pattern.");
        if (!int.TryParse(hour, out var hr) || hr < 0 || hr > 23)
            throw new UnsupportedScheduleException(cron, "Hour must be a fixed value (0-23) for this pattern.");

        var startTime = $"{hr:D2}:{min:D2}";

        // Pattern: M H D * * → MONTHLY
        if (dom != "*/1" && dow == "*/1")
        {
            if (!int.TryParse(dom, out var day) || day < 1 || day > 31)
                throw new UnsupportedScheduleException(cron, "Day of month must be 1-31.");

            return new SchtasksTrigger(ScheduleType.Monthly, 1, startTime,
                DayOfMonth: day,
                Description: $"Monthly on day {day} at {startTime}");
        }

        // Pattern: M H * * dow → WEEKLY (or DAILY if dow == *)
        if (dom == "*/1" && dow != "*/1")
        {
            var days = ParseDays(dow, cron);
            var allDays = new[] { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };

            if (days.Length == 7 || days.OrderBy(d => d).SequenceEqual(allDays.OrderBy(d => d)))
            {
                return new SchtasksTrigger(ScheduleType.Daily, 1, startTime,
                    Description: $"Daily at {startTime}");
            }

            return new SchtasksTrigger(ScheduleType.Weekly, 1, startTime,
                Days: days,
                Description: $"Weekly on {string.Join(", ", days)} at {startTime}");
        }

        // Pattern: M H * * * → DAILY
        if (dom == "*/1" && dow == "*/1")
        {
            return new SchtasksTrigger(ScheduleType.Daily, 1, startTime,
                Description: $"Daily at {startTime}");
        }

        throw new UnsupportedScheduleException(cron,
            "This cron pattern cannot be mapped to schtasks. " +
            "Supported: */N * * * *, 0 */N * * *, M H * * *, M H * * dow, M H D * *");
    }

    /// <summary>Parses a cron day-of-week field (ranges and lists) into schtasks day names.</summary>
    static string[] ParseDays(string dow, string cron)
    {
        var result = new List<string>();

        foreach (var segment in dow.Split(','))
        {
            // Handle range: 1-5
            if (segment.Contains('-'))
            {
                var rangeParts = segment.Split('-');
                if (rangeParts.Length != 2
                    || !int.TryParse(rangeParts[0], out var start)
                    || !int.TryParse(rangeParts[1], out var end))
                    throw new UnsupportedScheduleException(cron, $"Invalid day range: {segment}");

                for (var i = start; i <= end; i++)
                {
                    var key = i.ToString();
                    if (!DayMap.TryGetValue(key, out var day))
                        throw new UnsupportedScheduleException(cron, $"Invalid day number: {i}");
                    result.Add(day);
                }
            }
            else
            {
                if (!DayMap.TryGetValue(segment, out var day))
                    throw new UnsupportedScheduleException(cron, $"Invalid day: {segment}");
                result.Add(day);
            }
        }

        return result.Distinct().ToArray();
    }
}

/// <summary>
/// Thrown when a cron expression cannot be mapped to schtasks parameters.
/// </summary>
public sealed class UnsupportedScheduleException : Exception
{
    /// <summary>The cron expression that could not be mapped.</summary>
    public string CronExpression { get; }

    /// <summary>Initializes a new instance with the unsupported cron expression and reason.</summary>
    public UnsupportedScheduleException(string cron, string reason)
        : base($"Cannot map cron '{cron}' to schtasks: {reason}")
    {
        CronExpression = cron;
    }
}
