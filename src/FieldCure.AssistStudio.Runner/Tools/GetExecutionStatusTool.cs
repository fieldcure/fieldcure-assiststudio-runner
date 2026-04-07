using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace FieldCure.AssistStudio.Runner.Tools;

/// <summary>MCP tool for checking the status of an ongoing or completed execution.</summary>
[McpServerToolType]
public static class GetExecutionStatusTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc cref="GetExecutionStatusTool"/>
    [McpServerTool(Name = "get_execution_status")]
    [Description(
        "Checks the current status of a task execution. " +
        "Use this after run_task to monitor progress of a running execution.")]
    public static async Task<string> GetExecutionStatus(
        TaskStore store,
        [Description("Execution ID to check")]
        string execution_id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = await store.GetExecutionAsync(execution_id);
            if (execution is null)
                return JsonSerializer.Serialize(new { success = false, error = $"Execution '{execution_id}' not found." }, JsonOptions);

            var task = await store.GetTaskAsync(execution.TaskId);
            var now = DateTimeOffset.UtcNow;
            var duration = execution.CompletedAt.HasValue
                ? (execution.CompletedAt.Value - execution.StartedAt).TotalSeconds
                : (now - execution.StartedAt).TotalSeconds;

            return JsonSerializer.Serialize(new
            {
                execution_id = execution.Id,
                task_name = task?.Name,
                status = execution.Status.ToString(),
                started_at = execution.StartedAt.ToString("o"),
                completed_at = execution.CompletedAt?.ToString("o"),
                rounds_executed = execution.RoundsExecuted,
                result_summary = execution.ResultSummary,
                error_message = execution.ErrorMessage,
                notification_status = execution.NotificationStatus,
                duration_seconds = Math.Round(duration, 1),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
