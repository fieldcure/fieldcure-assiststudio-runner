using System.Text.Json;
using FieldCure.AssistStudio.Models;
using FieldCure.AssistStudio.Providers;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Storage;
using Microsoft.Extensions.Logging;

namespace FieldCure.AssistStudio.Runner.Execution;

/// <summary>
/// Core execution engine for Runner tasks.
/// Uses IAiProvider.CompleteAsync() for non-streaming headless execution.
/// Used by both exec mode (CLI) and serve mode (run_task).
/// </summary>
public sealed class TaskExecutor
{
    /// <summary>
    /// Tools that are always allowed regardless of the task's AllowedTools setting.
    /// These tools have no side effects and provide context/computation only.
    /// </summary>
    internal static readonly HashSet<string> SafeTools = ["get_environment", "run_javascript"];

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

            // ── Phase 3: LLM Loop ───────────────────────────────────────
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, task.Prompt)
            };

            var systemPrompt = BuildSystemPrompt(task);
            var toolList = tools.Cast<IAssistTool>().ToList();

            for (var round = 1; round <= task.Guardrails.MaxRounds; round++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                _logger.LogDebug("Round {Round}/{Max}", round, task.Guardrails.MaxRounds);

                var request = new AiRequest
                {
                    Messages = messages,
                    SystemPrompt = systemPrompt,
                    Temperature = preset.Temperature,
                    MaxTokens = preset.MaxTokens,
                    Tools = toolList.Count > 0 ? toolList : null,
                };

                AiResponse response;
                try
                {
                    response = await CompleteWithRetryAsync(provider, request, linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    execution.Status = ExecutionStatus.TimedOut;
                    execution.ErrorMessage = $"Execution timed out after {task.Guardrails.TimeoutSeconds} seconds.";
                    break;
                }

                var roundLog = new RoundLog { Round = round };

                // No tool calls → success
                if (!response.HasToolCalls)
                {
                    execution.Status = ExecutionStatus.Succeeded;
                    execution.ResultSummary = response.Content?.Trim();
                    roundLog.Response = new RoundMessage
                    {
                        Role = "assistant",
                        Content = response.Content,
                    };
                    executionLog.Rounds.Add(roundLog);
                    break;
                }

                // Process tool calls
                var assistantMsg = new ChatMessage(ChatRole.Assistant, response.Content ?? "")
                {
                    ToolCalls = response.ToolCalls.ToList(),
                };
                messages.Add(assistantMsg);

                roundLog.Response = new RoundMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls.Select(tc => new ToolCallLog
                    {
                        Id = tc.Id,
                        Name = tc.FunctionName,
                        Arguments = tc.Arguments,
                    }).ToList(),
                };
                roundLog.ToolResults = [];

                foreach (var toolCall in response.ToolCalls)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    string toolResult;
                    if (task.Guardrails.AllowedTools is not null
                        && !task.Guardrails.AllowedTools.Contains(toolCall.FunctionName)
                        && !SafeTools.Contains(toolCall.FunctionName))
                    {
                        toolResult = $"DENIED: Tool '{toolCall.FunctionName}' is not in the allowed tools list.";
                        _logger.LogWarning("Denied tool call: {ToolName}", toolCall.FunctionName);
                    }
                    else
                    {
                        try
                        {
                            var tool = tools.FirstOrDefault(t => t.Name == toolCall.FunctionName);
                            if (tool is null)
                            {
                                toolResult = $"Error: Tool '{toolCall.FunctionName}' not found.";
                            }
                            else
                            {
                                var args = JsonDocument.Parse(toolCall.Arguments).RootElement;
                                toolResult = await tool.ExecuteAsync(args, linkedCts.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            toolResult = $"Error executing tool '{toolCall.FunctionName}': {ex.Message}";
                            _logger.LogWarning(ex, "Tool call failed: {ToolName}", toolCall.FunctionName);
                        }
                    }

                    messages.Add(new ChatMessage(ChatRole.Tool, toolResult)
                    {
                        ToolCallId = toolCall.Id,
                    });

                    roundLog.ToolResults.Add(new ToolResultLog
                    {
                        ToolCallId = toolCall.Id,
                        Content = toolResult,
                    });
                }

                executionLog.Rounds.Add(roundLog);
                execution.RoundsExecuted = round;

                // Check if max rounds reached
                if (round == task.Guardrails.MaxRounds)
                {
                    execution.Status = ExecutionStatus.Failed;
                    execution.ErrorMessage = $"Maximum rounds ({task.Guardrails.MaxRounds}) reached without completion.";
                }
            }

            // ── Phase 4: Summarize ──────────────────────────────────────
            if (execution.Status == ExecutionStatus.Running)
            {
                execution.Status = ExecutionStatus.Succeeded;
            }

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

    static string BuildSystemPrompt(RunnerTask task)
    {
        var toolsInfo = task.Guardrails.AllowedTools is { Count: > 0 }
            ? string.Join(", ", task.Guardrails.AllowedTools)
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

    async Task<AiResponse> CompleteWithRetryAsync(
        IAiProvider provider, AiRequest request, CancellationToken ct)
    {
        var maxAttempts = _globalConfig.Retry.MaxAttempts;
        var delayMs = _globalConfig.Retry.InitialDelayMs;
        var multiplier = _globalConfig.Retry.BackoffMultiplier;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await provider.CompleteAsync(request, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "LLM API attempt {Attempt}/{Max} failed, retrying in {Delay}ms",
                    attempt, maxAttempts, delayMs);
                await Task.Delay(delayMs, ct);
                delayMs = (int)(delayMs * multiplier);
            }
        }
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
