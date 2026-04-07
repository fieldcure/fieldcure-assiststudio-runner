using FieldCure.AssistStudio.Runner.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Manages Windows Task Scheduler entries for Runner tasks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SchedulerService
{
    /// <summary>Prefix for all Runner task names registered in Windows Task Scheduler.</summary>
    const string TaskNamePrefix = "AssistStudio_Runner_";

    /// <summary>Global runner configuration for resolving tool paths.</summary>
    readonly RunnerConfig _config;

    /// <summary>Logger instance for scheduler diagnostics.</summary>
    readonly ILogger<SchedulerService> _logger;

    /// <summary>Initializes a new <see cref="SchedulerService"/> with configuration and logger.</summary>
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
        if (string.IsNullOrEmpty(task.Schedule) && !task.ScheduleOnce.HasValue)
            return new ScheduleResult(false, "No schedule defined for this task.");

        SchtasksTrigger trigger;
        if (task.ScheduleOnce.HasValue)
        {
            var local = task.ScheduleOnce.Value.ToLocalTime();
            trigger = new SchtasksTrigger(ScheduleType.Once, 1,
                local.ToString("HH:mm"),
                StartDate: local.ToString("yyyy/MM/dd"),
                Description: $"Once at {local:yyyy-MM-dd HH:mm}");
        }
        else
        {
            try
            {
                trigger = CronToSchtasks.Convert(task.Schedule!);
            }
            catch (UnsupportedScheduleException ex)
            {
                return new ScheduleResult(false, ex.Message);
            }
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

    /// <summary>Resolves the absolute path to the assiststudio-runner executable.</summary>
    string ResolveToolPath()
    {
        if (!string.IsNullOrEmpty(_config.ToolPath))
            return _config.ToolPath;

        // Primary: AssistStudio local tool install path
        var localToolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FieldCure", "AssistStudio", "tools", "assiststudio-runner.exe");
        if (File.Exists(localToolPath))
            return localToolPath;

        // Fallback: global dotnet tool
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalToolPath = Path.Combine(userProfile, ".dotnet", "tools", "assiststudio-runner.exe");
        if (File.Exists(globalToolPath))
            return globalToolPath;

        // Last resort: assume on PATH
        return "assiststudio-runner";
    }

    /// <summary>Runs schtasks.exe with the given arguments and returns the result.</summary>
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
