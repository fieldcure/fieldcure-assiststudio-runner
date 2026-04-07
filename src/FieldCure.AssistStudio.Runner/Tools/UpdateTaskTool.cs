using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Scheduling;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace FieldCure.AssistStudio.Runner.Tools;

/// <summary>MCP tool for partially updating a task's definition, schedule, or configuration.</summary>
[McpServerToolType]
public static class UpdateTaskTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc cref="UpdateTaskTool"/>
    [McpServerTool(Name = "update_task", Destructive = true)]
    [Description(
        "Updates an existing Runner task. Only provided fields are changed (partial update). " +
        "Use schedule='__remove__' to remove a schedule. " +
        "Schedule changes automatically update Windows Task Scheduler.")]
    public static async Task<string> UpdateTask(
        TaskStore store,
        SchedulerService scheduler,
        [Description("Task ID to update")]
        string task_id,
        [Description("New task name")]
        string? name = null,
        [Description("New workflow prompt")]
        string? prompt = null,
        [Description("New description")]
        string? description = null,
        [Description("New cron schedule. Use '__remove__' to remove schedule.")]
        string? schedule = null,
        [Description("Enable or disable the task")]
        bool? is_enabled = null,
        [Description("New maximum rounds")]
        int? max_rounds = null,
        [Description("New timeout in seconds")]
        int? timeout_seconds = null,
        [Description("New allowed tools (JSON array). Use 'null' string to clear.")]
        string? allowed_tools = null,
        [Description("New provider preset name")]
        string? preset_name = null,
        [Description("New MCP servers (JSON array)")]
        string? mcp_servers = null,
        [Description("New output channel")]
        string? output_channel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await store.GetTaskAsync(task_id);
            if (task is null)
                return JsonSerializer.Serialize(new { success = false, error = $"Task '{task_id}' not found." }, JsonOptions);

            var updatedFields = new List<string>();
            var oldSchedule = task.Schedule;

            if (name is not null) { task.Name = name; updatedFields.Add("name"); }
            if (prompt is not null) { task.Prompt = prompt; updatedFields.Add("prompt"); }
            if (description is not null) { task.Description = description; updatedFields.Add("description"); }

            if (schedule is not null)
            {
                if (schedule == "__remove__")
                {
                    task.Schedule = null;
                }
                else
                {
                    try { CronToSchtasks.Convert(schedule); }
                    catch (UnsupportedScheduleException ex)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
                    }
                    task.Schedule = schedule;
                }
                updatedFields.Add("schedule");
            }

            if (is_enabled.HasValue) { task.IsEnabled = is_enabled.Value; updatedFields.Add("is_enabled"); }
            if (max_rounds.HasValue) { task.Guardrails.MaxRounds = max_rounds.Value; updatedFields.Add("max_rounds"); }
            if (timeout_seconds.HasValue) { task.Guardrails.TimeoutSeconds = timeout_seconds.Value; updatedFields.Add("timeout_seconds"); }

            if (allowed_tools is not null)
            {
                if (allowed_tools == "null")
                {
                    task.Guardrails.AllowedTools = null;
                }
                else
                {
                    try
                    {
                        task.Guardrails.AllowedTools = JsonSerializer.Deserialize<List<string>>(allowed_tools, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = $"Invalid allowed_tools: {ex.Message}" }, JsonOptions);
                    }
                }
                updatedFields.Add("allowed_tools");
            }

            if (preset_name is not null) { task.PresetName = preset_name; updatedFields.Add("preset_name"); }
            if (output_channel is not null) { task.OutputChannel = output_channel; updatedFields.Add("output_channel"); }

            if (mcp_servers is not null)
            {
                try
                {
                    task.McpServers = JsonSerializer.Deserialize<List<McpServerConfig>>(mcp_servers, JsonOptions) ?? [];
                }
                catch (JsonException ex)
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"Invalid mcp_servers: {ex.Message}" }, JsonOptions);
                }
                McpServerConfig.ResolveCommands(task.McpServers);
                updatedFields.Add("mcp_servers");
            }

            if (updatedFields.Count == 0)
                return JsonSerializer.Serialize(new { success = true, updated_fields = Array.Empty<string>(), summary = "No fields to update." }, JsonOptions);

            task.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateTaskAsync(task);

            // Handle schtasks changes
            bool? scheduleRegistered = null;
            if (updatedFields.Contains("schedule"))
            {
                // Remove old schedule
                if (oldSchedule is not null)
                    await scheduler.UnregisterAsync(task_id);

                // Register new schedule
                if (task.Schedule is not null)
                {
                    var result = await scheduler.RegisterAsync(task);
                    scheduleRegistered = result.Success;
                }
            }
            else if (updatedFields.Contains("is_enabled") && task.Schedule is not null)
            {
                var result = await scheduler.SetEnabledAsync(task_id, task.IsEnabled);
                scheduleRegistered = result.Success;
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                updated_fields = updatedFields.ToArray(),
                schedule_registered = scheduleRegistered,
                summary = $"Task '{task.Name}' updated: {string.Join(", ", updatedFields)}",
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
