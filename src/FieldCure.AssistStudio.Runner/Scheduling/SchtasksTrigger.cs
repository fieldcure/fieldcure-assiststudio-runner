namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Supported schedule types mappable to schtasks.
/// </summary>
public enum ScheduleType
{
    /// <summary>Every N minutes.</summary>
    Minute,
    /// <summary>Every N hours.</summary>
    Hourly,
    /// <summary>Once per day.</summary>
    Daily,
    /// <summary>On specified days of the week.</summary>
    Weekly,
    /// <summary>On a specified day of the month.</summary>
    Monthly,
}

/// <summary>
/// Structured schtasks trigger information.
/// </summary>
public sealed record SchtasksTrigger(
    ScheduleType Type,
    int Interval,
    string StartTime,
    string[]? Days = null,
    int? DayOfMonth = null,
    string? Description = null)
{
    /// <summary>
    /// Converts this trigger to schtasks.exe command-line arguments.
    /// </summary>
    public string ToSchtasksArgs() => Type switch
    {
        ScheduleType.Minute => $"/SC MINUTE /MO {Interval}",
        ScheduleType.Hourly => $"/SC HOURLY /MO {Interval} /ST {StartTime}",
        ScheduleType.Daily => $"/SC DAILY /ST {StartTime}",
        ScheduleType.Weekly when Days is { Length: > 0 } =>
            $"/SC WEEKLY /D {string.Join(",", Days)} /ST {StartTime}",
        ScheduleType.Weekly => $"/SC WEEKLY /ST {StartTime}",
        ScheduleType.Monthly when DayOfMonth.HasValue =>
            $"/SC MONTHLY /D {DayOfMonth.Value} /ST {StartTime}",
        ScheduleType.Monthly => $"/SC MONTHLY /ST {StartTime}",
        _ => throw new InvalidOperationException($"Unsupported schedule type: {Type}")
    };
}

/// <summary>
/// Result of a scheduler operation.
/// </summary>
public sealed record ScheduleResult(bool Success, string? ErrorMessage = null);
