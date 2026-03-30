using System.ComponentModel;
using System.Text.Json;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.AssistStudio.Runner.Tools;

[McpServerToolType]
public static class GetTaskHistoryTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(Name = "get_task_history")]
    [Description(
        "Retrieves execution history for a Runner task. " +
        "Returns recent executions with status, timing, and result summaries.")]
    public static async Task<string> GetTaskHistory(
        TaskStore store,
        [Description("Task ID to query history for")]
        string task_id,
        [Description("Maximum number of executions to return (default: 10)")]
        int? limit = null,
        [Description("Filter by status: 'Succeeded', 'Failed', 'TimedOut', or 'all' (default: 'all')")]
        string? status_filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await store.GetTaskAsync(task_id);
            if (task is null)
                return JsonSerializer.Serialize(new { success = false, error = $"Task '{task_id}' not found." }, JsonOptions);

            var executions = await store.GetExecutionsAsync(task_id, limit ?? 10, status_filter);
            var totalCount = await store.GetExecutionCountAsync(task_id);

            var executionList = executions.Select(e => new
            {
                id = e.Id,
                status = e.Status.ToString(),
                started_at = e.StartedAt.ToString("o"),
                completed_at = e.CompletedAt?.ToString("o"),
                rounds_executed = e.RoundsExecuted,
                result_summary = e.ResultSummary,
                error_message = e.ErrorMessage,
                notification_status = e.NotificationStatus,
                log_path = e.LogPath,
                duration_seconds = e.CompletedAt.HasValue
                    ? Math.Round((e.CompletedAt.Value - e.StartedAt).TotalSeconds, 1)
                    : (double?)null,
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                task_name = task.Name,
                executions = executionList,
                total_count = totalCount,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
