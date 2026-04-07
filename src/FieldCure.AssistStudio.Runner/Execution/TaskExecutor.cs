using System.Text.Json;
using FieldCure.Ai.Execution;
using FieldCure.Ai.Providers;
using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Storage;
using Microsoft.Extensions.Logging;

namespace FieldCure.AssistStudio.Runner.Execution;

/// <summary>
/// Core execution engine for Runner tasks.
/// Delegates the LLM loop to <see cref="AgentLoop"/> for shared execution logic.
/// Used by both exec mode (CLI) and serve mode (run_task).
/// </summary>
public sealed class TaskExecutor
{
    readonly TaskStore _taskStore;
    readonly RunnerConfig _globalConfig;
    readonly ICredentialService _credentialService;
    readonly ILogger<TaskExecutor> _logger;

    public TaskExecutor(
        TaskStore taskStore,
        RunnerConfig globalConfig,
        ICredentialService credentialService,
        ILogger<TaskExecutor> logger)
    {
        _taskStore = taskStore;
        _globalConfig = globalConfig;
        _credentialService = credentialService;
        _logger = logger;
    }

    /// <summary>
    /// Executes a task and returns the completed execution record.
    /// </summary>
    public async Task<TaskExecution> ExecuteAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        // ── Phase 1: Initialize ──────────────────────────────────────────
        var task = await _taskStore.GetTaskAsync(taskId)
            ?? throw new TaskNotFoundException(taskId);

        if (await _taskStore.HasRunningExecutionAsync(taskId))
            throw new AlreadyRunningException(taskId);

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = taskId,
            Status = ExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _taskStore.InsertExecutionAsync(execution);

        var dataDir = _globalConfig.GetEffectiveDataDirectory();
        var logPath = $"logs/{execution.Id}.json";
        var logFullPath = Path.Combine(dataDir, logPath);

        var executionLog = new ExecutionLog
        {
            ExecutionId = execution.Id,
            TaskId = taskId,
            TaskName = task.Name,
            StartedAt = execution.StartedAt,
        };

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(task.Guardrails.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource
            .CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        _logger.LogInformation("Starting execution {ExecutionId} for task '{TaskName}' ({TaskId})",
            execution.Id, task.Name, taskId);

        McpServerPool? pool = null;

        try
        {
            // Resolve provider
            var presetName = task.PresetName ?? _globalConfig.DefaultPresetName;
            if (presetName is null)
                throw new InvalidOperationException("No preset configured. Set task PresetName or runner.json defaultPresetName.");

            var preset = _globalConfig.ResolvePreset(presetName)
                ?? throw new InvalidOperationException($"Preset '{presetName}' not found in runner.json.");

            preset.ApiKey = _credentialService.GetApiKey(preset.ProviderType)
                ?? throw new InvalidOperationException($"API key for provider '{preset.ProviderType}' not found in PasswordVault.");

            var provider = ProviderFactory.Create(preset);
            _logger.LogDebug("Using provider {Provider} model {Model}", preset.ProviderType, preset.ModelId);

            // ── Phase 2: Bootstrap MCP Servers ───────────────────────────
            pool = new McpServerPool(_logger);
            var mergedServers = MergeMcpServers(task);
            var tools = await pool.BootstrapAsync(
                mergedServers, task.Guardrails.AllowedTools, _credentialService);

            // ── Phase 3: LLM Loop (delegated to AgentLoop) ────────────
            var agentLoop = new AgentLoop();
            var toolNames = tools.Select(t => t.Name).ToList();
            var loopContext = new AgentLoopContext
            {
                Provider = provider,
                SystemPrompt = BuildSystemPrompt(task, toolNames),
                UserPrompt = task.Prompt,
                Tools = tools.Cast<IAssistTool>().ToList(),
                MaxRounds = task.Guardrails.MaxRounds,
                Temperature = preset.Temperature,
                MaxTokens = preset.MaxTokens,
            };

            var loopResult = await agentLoop.RunAsync(loopContext, linkedCts.Token);

            // ── Phase 4: Map result ─────────────────────────────────────
            execution.RoundsExecuted = loopResult.RoundsExecuted;
            execution.ResultSummary = loopResult.Summary;

            execution.Status = loopResult.Status switch
            {
                AgentLoopStatus.Completed => ExecutionStatus.Succeeded,
                AgentLoopStatus.MaxRoundsReached => ExecutionStatus.Failed,
                _ => ExecutionStatus.Failed,
            };

            if (loopResult.Status != AgentLoopStatus.Completed)
                execution.ErrorMessage = loopResult.ErrorMessage;

            // ── Phase 5: Notify ─────────────────────────────────────────
            await TryNotifyAsync(task, execution, pool);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            execution.Status = ExecutionStatus.TimedOut;
            execution.ErrorMessage = $"Execution timed out after {task.Guardrails.TimeoutSeconds} seconds.";
        }
        catch (Exception ex)
        {
            execution.Status = ExecutionStatus.Failed;
            execution.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Task execution failed");
        }
        finally
        {
            // ── Phase 6: Cleanup ────────────────────────────────────────
            execution.CompletedAt = DateTimeOffset.UtcNow;
            execution.LogPath = logPath;

            executionLog.CompletedAt = execution.CompletedAt;
            executionLog.Status = execution.Status.ToString();
            executionLog.NotificationStatus = execution.NotificationStatus;

            try { executionLog.Save(logFullPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to save execution log"); }

            try { await _taskStore.UpdateExecutionAsync(execution); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to update execution record"); }

            if (pool is not null)
            {
                try { await pool.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error during MCP pool cleanup"); }
            }

            CleanOldLogs(dataDir);

            _logger.LogInformation("Execution {ExecutionId} completed: {Status}",
                execution.Id, execution.Status);
        }

        return execution;
    }

    static string BuildSystemPrompt(RunnerTask task, IReadOnlyList<string> actualTools)
    {
        var toolsInfo = actualTools.Count > 0
            ? string.Join(", ", actualTools)
            : "No tools are available.";

        return $"""
            You are an autonomous task executor running in headless mode.
            There is no human in the loop — execute the task to completion.

            RULES:
            - Use only the tools provided. Do not ask for human input.
            - When the task is complete, respond with a concise summary.
            - If you cannot complete the task, explain why in your final response.
            - You have a maximum of {task.Guardrails.MaxRounds} rounds.
            - Available tools: {toolsInfo}
            """;
    }

    async Task TryNotifyAsync(RunnerTask task, TaskExecution execution, McpServerPool pool)
    {
        if (string.IsNullOrEmpty(task.OutputChannel))
        {
            execution.NotificationStatus = "skipped";
            return;
        }

        var sendMessage = pool.GetTool("send_message");
        if (sendMessage is null)
        {
            _logger.LogWarning("OutputChannel is set but no 'send_message' tool available");
            execution.NotificationStatus = "skipped";
            return;
        }

        try
        {
            var messageContent = execution.Status == ExecutionStatus.Succeeded
                ? execution.ResultSummary ?? "Task completed successfully."
                : $"Task failed: {execution.ErrorMessage}";

            var args = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                channel = task.OutputChannel,
                message = messageContent,
            })).RootElement;

            await sendMessage.ExecuteAsync(args);
            execution.NotificationStatus = "sent";
            _logger.LogInformation("Notification sent to channel '{Channel}'", task.OutputChannel);
        }
        catch (Exception ex)
        {
            execution.NotificationStatus = "failed";
            _logger.LogWarning(ex, "Failed to send notification to channel '{Channel}'", task.OutputChannel);
        }

        // Fallback notification on failure
        if (execution.Status != ExecutionStatus.Succeeded
            && !string.IsNullOrEmpty(_globalConfig.FallbackChannel)
            && _globalConfig.FallbackChannel != task.OutputChannel)
        {
            try
            {
                var args = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    channel = _globalConfig.FallbackChannel,
                    message = $"[Runner Alert] Task '{task.Name}' failed: {execution.ErrorMessage}",
                })).RootElement;

                await sendMessage.ExecuteAsync(args);
                _logger.LogInformation("Fallback notification sent to '{Channel}'", _globalConfig.FallbackChannel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send fallback notification");
            }
        }
    }

    void CleanOldLogs(string dataDir)
    {
        if (_globalConfig.LogRetentionDays <= 0) return;

        try
        {
            var logsDir = Path.Combine(dataDir, "logs");
            if (!Directory.Exists(logsDir)) return;

            var cutoff = DateTime.UtcNow.AddDays(-_globalConfig.LogRetentionDays);
            foreach (var file in Directory.GetFiles(logsDir, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogDebug("Cleaned old log: {File}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning old logs");
        }
    }

    /// <summary>
    /// Merges default MCP servers from config with task-specific servers.
    /// Task servers take precedence for duplicate names.
    /// Falls back to auto-detecting installed stateless servers when no servers are configured.
    /// </summary>
    List<McpServerConfig> MergeMcpServers(RunnerTask task)
    {
        if (task.ExcludeDefaultServers)
            return task.McpServers;

        var merged = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _globalConfig.DefaultMcpServers)
            merged[entry.Name] = entry.ToMcpServerConfig();

        foreach (var server in task.McpServers)
            merged[server.Id] = server; // task-specific servers win

        // Auto-detect installed stateless servers when nothing is configured
        if (merged.Count == 0)
        {
            _logger.LogInformation("No MCP servers configured — auto-detecting installed servers");
            foreach (var entry in RunnerConfig.DetectInstalledServers())
                merged[entry.Name] = entry.ToMcpServerConfig();
        }

        return [.. merged.Values];
    }
}

/// <summary>Thrown when a task is not found in the store.</summary>
public sealed class TaskNotFoundException : Exception
{
    public string TaskId { get; }
    public TaskNotFoundException(string taskId) : base($"Task '{taskId}' not found.") => TaskId = taskId;
}

/// <summary>Thrown when a task already has a running execution.</summary>
public sealed class AlreadyRunningException : Exception
{
    public string TaskId { get; }
    public AlreadyRunningException(string taskId) : base($"Task '{taskId}' already has a running execution.") => TaskId = taskId;
}
