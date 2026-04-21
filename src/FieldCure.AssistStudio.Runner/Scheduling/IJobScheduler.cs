using FieldCure.AssistStudio.Runner.Models;

namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Platform-specific scheduler abstraction for registering and reconciling Runner jobs.
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Registers or replaces the scheduled job for the given task.
    /// Safe to call repeatedly: an existing registration with the same task id is overwritten.
    /// </summary>
    Task<ScheduleResult> RegisterAsync(RunnerTask task);

    /// <summary>
    /// Removes the scheduled job for the given task id.
    /// Returns success even if no registration currently exists.
    /// </summary>
    Task<ScheduleResult> UnregisterAsync(string taskId);

    /// <summary>
    /// Enables or disables an existing scheduled job.
    /// Fails when the task id is not registered.
    /// </summary>
    Task<ScheduleResult> SetEnabledAsync(string taskId, bool enabled);

    /// <summary>
    /// Returns the task ids currently registered by this Runner instance.
    /// Only entries owned by this Runner's fixed scheduler prefix are included.
    /// </summary>
    Task<IReadOnlyList<string>> ListRegisteredIdsAsync();
}
