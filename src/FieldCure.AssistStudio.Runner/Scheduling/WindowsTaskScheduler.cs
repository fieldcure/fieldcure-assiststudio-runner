using FieldCure.AssistStudio.Runner.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FieldCure.AssistStudio.Runner.Scheduling;

/// <summary>
/// Manages Windows Task Scheduler entries for Runner tasks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler : IJobScheduler
{
    /// <summary>Prefix for all Runner task names registered in Windows Task Scheduler.</summary>
    const string TaskNamePrefix = "AssistStudio_Runner_";

    /// <summary>Global runner configuration for resolving tool paths.</summary>
    readonly RunnerConfig _config;

    /// <summary>Logger instance for scheduler diagnostics.</summary>
    readonly ILogger<WindowsTaskScheduler> _logger;

    /// <summary>Initializes a new <see cref="WindowsTaskScheduler"/> with configuration and logger.</summary>
    public WindowsTaskScheduler(RunnerConfig config, ILogger<WindowsTaskScheduler> logger)
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
        var result = await RunSchtasksAsync($"/DELETE /TN \"{taskName}\" /F");
        if (!result.Success && IsTaskMissing(result.ErrorMessage))
            return new ScheduleResult(true);
        return result;
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

    /// <summary>
    /// Lists task ids currently registered in Windows Task Scheduler by this Runner.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRegisteredIdsAsync()
    {
        var result = await RunSchtasksCaptureAsync("/QUERY /FO CSV /NH");
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        var ids = new List<string>();
        using var reader = new StringReader(result.StandardOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var taskName = ParseFirstCsvField(line);
            if (string.IsNullOrWhiteSpace(taskName))
                continue;

            taskName = taskName.Trim().TrimStart('\\');
            if (!taskName.StartsWith(TaskNamePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            ids.Add(taskName[TaskNamePrefix.Length..]);
        }

        return ids;
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
        var result = await RunSchtasksCaptureAsync(arguments);
        if (!result.Success)
            return new ScheduleResult(false, result.ErrorMessage);

        _logger.LogDebug("schtasks succeeded: {Output}", result.StandardOutput.Trim());
        return new ScheduleResult(true);
    }

    /// <summary>Runs schtasks.exe with the given arguments and captures raw process output.</summary>
    async Task<SchtasksProcessResult> RunSchtasksCaptureAsync(string arguments)
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
                return new SchtasksProcessResult(false, stdout, error);
            }

            return new SchtasksProcessResult(true, stdout, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run schtasks");
            return new SchtasksProcessResult(false, string.Empty, ex.Message);
        }
    }

    /// <summary>Returns true when schtasks reports a missing scheduled task entry.</summary>
    static bool IsTaskMissing(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        return errorMessage.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("cannot find the task", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("the system cannot find the file specified", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("no scheduled task", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the first field from a CSV row emitted by schtasks /FO CSV.</summary>
    static string ParseFirstCsvField(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return comma >= 0 ? line[..comma] : line;
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    readonly record struct SchtasksProcessResult(bool Success, string StandardOutput, string? ErrorMessage);
}
