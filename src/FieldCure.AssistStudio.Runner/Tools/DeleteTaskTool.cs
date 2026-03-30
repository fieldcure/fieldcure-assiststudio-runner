using System.ComponentModel;
using System.Text.Json;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Scheduling;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.AssistStudio.Runner.Tools;

[McpServerToolType]
public static class DeleteTaskTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(Name = "delete_task", Destructive = true)]
    [Description(
        "Deletes a Runner task and all its execution history. " +
        "Also removes the associated Windows Task Scheduler entry and log files. " +
        "Cannot delete a task that is currently running.")]
    public static async Task<string> DeleteTask(
        TaskStore store,
        SchedulerService scheduler,
        RunnerConfig config,
        [Description("Task ID to delete")]
        string task_id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await store.GetTaskAsync(task_id);
            if (task is null)
                return JsonSerializer.Serialize(new { success = false, error = $"Task '{task_id}' not found." }, JsonOptions);

            // Reject if running
            if (await store.HasRunningExecutionAsync(task_id))
                return JsonSerializer.Serialize(new { success = false, error = "Cannot delete a task that is currently running." }, JsonOptions);

            // Unregister from schtasks
            if (task.Schedule is not null)
                await scheduler.UnregisterAsync(task_id);

            // Delete from store (cascades executions)
            var (executionsRemoved, logPaths) = await store.DeleteTaskAsync(task_id);

            // Delete log files
            var dataDir = config.GetEffectiveDataDirectory();
            var logsRemoved = 0;
            foreach (var logPath in logPaths)
            {
                var fullPath = Path.Combine(dataDir, logPath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    logsRemoved++;
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                deleted = true,
                executions_removed = executionsRemoved,
                logs_removed = logsRemoved,
                summary = $"Task '{task.Name}' deleted. Removed {executionsRemoved} execution(s) and {logsRemoved} log file(s).",
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
