using System.Diagnostics;
using System.Runtime.Versioning;
using FieldCure.AssistStudio.Runner.Models;
using Microsoft.Extensions.Logging;

namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Manages Windows Task Scheduler entries for Runner tasks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SchedulerService
{
    const string TaskNamePrefix = "AssistStudio_Runner_";

    readonly RunnerConfig _config;
    readonly ILogger<SchedulerService> _logger;

    public SchedulerService(RunnerConfig config, ILogger<SchedulerService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Registers a scheduled task in Windows Task Scheduler.
    /// </summary>
    public async Task<ScheduleResult> RegisterAsync(RunnerTask task)
    {
        if (string.IsNullOrEmpty(task.Schedule))
            return new ScheduleResult(false, "No schedule defined for this task.");

        SchtasksTrigger trigger;
        try
        {
            trigger = CronToSchtasks.Convert(task.Schedule);
        }
        catch (UnsupportedScheduleException ex)
        {
            return new ScheduleResult(false, ex.Message);
        }

        var toolPath = ResolveToolPath();
        var taskName = $"{TaskNamePrefix}{task.Id}";
        var triggerArgs = trigger.ToSchtasksArgs();

        var args = $"/CREATE /TN \"{taskName}\" " +
                   $"/TR \"\\\"{toolPath}\\\" exec {task.Id}\" " +
                   $"{triggerArgs} /F /RL LIMITED /IT";

        return await RunSchtasksAsync(args);
    }

    /// <summary>
    /// Removes a scheduled task from Windows Task Scheduler.
    /// </summary>
    public async Task<ScheduleResult> UnregisterAsync(string taskId)
    {
        var taskName = $"{TaskNamePrefix}{taskId}";
        return await RunSchtasksAsync($"/DELETE /TN \"{taskName}\" /F");
    }

    /// <summary>
    /// Disables or enables a scheduled task without removing it.
    /// </summary>
    public async Task<ScheduleResult> SetEnabledAsync(string taskId, bool enabled)
    {
        var taskName = $"{TaskNamePrefix}{taskId}";
        var flag = enabled ? "/ENABLE" : "/DISABLE";
        return await RunSchtasksAsync($"/CHANGE /TN \"{taskName}\" {flag}");
    }

    string ResolveToolPath()
    {
        if (!string.IsNullOrEmpty(_config.ToolPath))
            return _config.ToolPath;

        // Default: assume it's on PATH as a dotnet tool
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(userProfile, ".dotnet", "tools", "assiststudio-runner.exe");

        return File.Exists(defaultPath) ? defaultPath : "assiststudio-runner";
    }

    async Task<ScheduleResult> RunSchtasksAsync(string arguments)
    {
        try
        {
            _logger.LogDebug("Running: schtasks {Arguments}", arguments);

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start schtasks process.");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                _logger.LogWarning("schtasks failed (exit {Code}): {Error}", process.ExitCode, error);
                return new ScheduleResult(false, error);
            }

            _logger.LogDebug("schtasks succeeded: {Output}", stdout.Trim());
            return new ScheduleResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run schtasks");
            return new ScheduleResult(false, ex.Message);
        }
    }
}
