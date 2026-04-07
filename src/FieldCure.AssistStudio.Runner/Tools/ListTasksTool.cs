using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace FieldCure.AssistStudio.Runner.Tools;

/// <summary>MCP tool for listing all tasks with optional filtering.</summary>
[McpServerToolType]
public static class ListTasksTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc cref="ListTasksTool"/>
    [McpServerTool(Name = "list_tasks")]
    [Description(
        "Lists all Runner tasks with optional filtering. " +
        "Returns task summaries including last execution status. " +
        "Does not include full prompts (use get_task for details).")]
    public static async Task<string> ListTasks(
        TaskStore store,
        [Description("Filter by status: 'enabled', 'disabled', or 'all' (default: 'all')")]
        string? status_filter = null,
        [Description("Filter by schedule presence: true = only scheduled, false = only manual")]
        bool? has_schedule = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tasks = await store.GetAllTasksAsync(status_filter, has_schedule);

            var taskSummaries = new List<object>();
            foreach (var task in tasks)
            {
                var lastExec = await store.GetLatestExecutionAsync(task.Id);

                taskSummaries.Add(new
                {
                    id = task.Id,
                    name = task.Name,
                    description = task.Description,
                    schedule = task.Schedule,
                    is_enabled = task.IsEnabled,
                    last_execution_status = lastExec?.Status.ToString(),
                    last_executed_at = lastExec?.StartedAt.ToString("o"),
                });
            }

            return JsonSerializer.Serialize(new
            {
                tasks = taskSummaries,
                total_count = taskSummaries.Count,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
