using System.ComponentModel;
using System.Text.Json;
using FieldCure.AssistStudio.Runner.Execution;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.AssistStudio.Runner.Tools;

[McpServerToolType]
public static class RunTaskTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(Name = "run_task", Destructive = true)]
    [Description(
        "Starts execution of a Runner task. By default returns immediately with the execution ID " +
        "(use get_execution_status to check progress). Set wait=true to wait up to 60 seconds for completion.")]
    public static async Task<string> RunTask(
        TaskStore store,
        TaskExecutor executor,
        [Description("Task ID to execute")]
        string task_id,
        [Description("If true, waits up to 60 seconds for completion before returning")]
        bool? wait = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate task exists
            var task = await store.GetTaskAsync(task_id);
            if (task is null)
                return JsonSerializer.Serialize(new { success = false, error = $"Task '{task_id}' not found." }, JsonOptions);

            // Check not already running
            if (await store.HasRunningExecutionAsync(task_id))
                return JsonSerializer.Serialize(new { success = false, error = $"Task '{task_id}' already has a running execution." }, JsonOptions);

            // Start execution (runs asynchronously — no Task.Run needed for async methods)
            var executionTask = executor.ExecuteAsync(task_id, cancellationToken);

            if (wait == true)
            {
                // Wait up to 60 seconds
                var completed = await Task.WhenAny(executionTask, Task.Delay(60_000, cancellationToken));

                if (completed == executionTask)
                {
                    var execution = await executionTask;
                    var duration = execution.CompletedAt.HasValue
                        ? (execution.CompletedAt.Value - execution.StartedAt).TotalSeconds
                        : 0;

                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        execution_id = execution.Id,
                        status = execution.Status.ToString(),
                        result_summary = execution.ResultSummary,
                        error_message = execution.ErrorMessage,
                        rounds_executed = execution.RoundsExecuted,
                        duration_seconds = Math.Round(duration, 1),
                    }, JsonOptions);
                }

                // Still running after 60s — find the execution ID from the store
                var latestExec = await store.GetLatestExecutionAsync(task_id);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    execution_id = latestExec?.Id,
                    status = "Running",
                    summary = "Task is still running. Use get_execution_status to check progress.",
                }, JsonOptions);
            }

            // Non-wait mode: wait briefly for execution record to be created
            await Task.Delay(500, cancellationToken);
            var exec = await store.GetLatestExecutionAsync(task_id);

            return JsonSerializer.Serialize(new
            {
                success = true,
                execution_id = exec?.Id,
                status = "Running",
                summary = "Execution started. Use get_execution_status to check progress.",
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
