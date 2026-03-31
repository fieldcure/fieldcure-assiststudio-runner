using System.ComponentModel;
using System.Text.Json;
using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Scheduling;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.AssistStudio.Runner.Tools;

[McpServerToolType]
public static class CreateTaskTool
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(Name = "create_task", Destructive = true)]
    [Description(
        "Creates a new Runner task with the given prompt, schedule, and configuration. " +
        "If a cron schedule is provided, automatically registers with Windows Task Scheduler. " +
        "Supported cron patterns: */N * * * * (every N min), 0 */N * * * (every N hours), " +
        "M H * * * (daily), M H * * 1-5 (weekdays), M H D * * (monthly).")]
    public static async Task<string> CreateTask(
        TaskStore store,
        SchedulerService scheduler,
        ICredentialService credentials,
        [Description("Human-readable name for the task")]
        string name,
        [Description("Natural language workflow prompt")]
        string prompt,
        [Description("MCP server configurations (array of objects with id, name, transportType, command, arguments, url, environmentVariableKeys)")]
        string mcp_servers,
        [Description("Optional description of the task")]
        string? description = null,
        [Description("Cron expression for scheduled execution (e.g. '0 9 * * 1-5' for weekdays at 9am). Null = manual only.")]
        string? schedule = null,
        [Description("Maximum LLM interaction rounds (default: 10)")]
        int? max_rounds = null,
        [Description("Execution timeout in seconds (default: 300)")]
        int? timeout_seconds = null,
        [Description("Tool names the LLM may invoke (JSON array). Null = no tools allowed.")]
        string? allowed_tools = null,
        [Description("Provider preset name. Null = use global default.")]
        string? preset_name = null,
        [Description("Outbox channel name for result notification")]
        string? output_channel = null,
        [Description("When true, default MCP servers from runner.json are not included. Default: false.")]
        bool? exclude_default_servers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse mcp_servers
            List<McpServerConfig> servers;
            try
            {
                servers = JsonSerializer.Deserialize<List<McpServerConfig>>(mcp_servers, JsonOptions) ?? [];
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = $"Invalid mcp_servers JSON: {ex.Message}" }, JsonOptions);
            }

            // Parse allowed_tools
            List<string>? tools = null;
            if (allowed_tools is not null)
            {
                try
                {
                    tools = JsonSerializer.Deserialize<List<string>>(allowed_tools, JsonOptions);
                }
                catch (JsonException ex)
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"Invalid allowed_tools JSON: {ex.Message}" }, JsonOptions);
                }
            }

            // Validate cron if present
            if (schedule is not null)
            {
                try { CronToSchtasks.Convert(schedule); }
                catch (UnsupportedScheduleException ex)
                {
                    return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
                }
            }

            // Check preset API key (warning only)
            string? presetWarning = null;
            if (preset_name is not null && credentials.GetApiKey(preset_name) is null)
                presetWarning = $"Warning: API key for preset '{preset_name}' not found in PasswordVault.";

            var now = DateTimeOffset.UtcNow;
            var task = new RunnerTask
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = description,
                Prompt = prompt,
                Schedule = schedule,
                IsEnabled = true,
                Guardrails = new TaskGuardrails
                {
                    MaxRounds = max_rounds ?? 10,
                    TimeoutSeconds = timeout_seconds ?? 300,
                    AllowedTools = tools,
                },
                PresetName = preset_name,
                McpServers = servers,
                ExcludeDefaultServers = exclude_default_servers ?? false,
                OutputChannel = output_channel,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await store.InsertTaskAsync(task);

            // Register with schtasks
            bool scheduleRegistered = false;
            string? scheduleError = null;
            if (schedule is not null)
            {
                var result = await scheduler.RegisterAsync(task);
                scheduleRegistered = result.Success;
                if (!result.Success)
                    scheduleError = result.ErrorMessage;
            }

            var summary = $"Task '{name}' created successfully.";
            if (scheduleRegistered)
                summary += $" Scheduled: {schedule}";
            else if (schedule is not null && !scheduleRegistered)
                summary += $" Schedule registration failed: {scheduleError}";
            if (presetWarning is not null)
                summary += $" {presetWarning}";

            return JsonSerializer.Serialize(new
            {
                success = true,
                task_id = task.Id,
                schedule_registered = schedule is not null ? scheduleRegistered : (bool?)null,
                summary,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
        }
    }
}
