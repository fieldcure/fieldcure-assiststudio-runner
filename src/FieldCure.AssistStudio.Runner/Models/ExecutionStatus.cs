namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Possible states of a task execution.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>Execution is queued but not yet started.</summary>
    Pending,

    /// <summary>Execution is currently in progress.</summary>
    Running,

    /// <summary>Execution completed successfully.</summary>
    Succeeded,

    /// <summary>Execution failed due to an error.</summary>
    Failed,

    /// <summary>Execution exceeded the configured timeout.</summary>
    TimedOut
}
