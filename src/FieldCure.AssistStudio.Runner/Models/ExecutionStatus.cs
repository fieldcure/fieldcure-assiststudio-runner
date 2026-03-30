namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Possible states of a task execution.
/// </summary>
public enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    TimedOut
}
