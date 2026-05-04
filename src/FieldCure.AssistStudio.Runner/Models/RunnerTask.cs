namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Represents a Runner task definition.
/// </summary>
public sealed class RunnerTask
{
    /// <summary>Unique identifier for this task.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name of the task.</summary>
    public required string Name { get; set; }

    /// <summary>Optional description of what this task does.</summary>
    public string? Description { get; set; }

    /// <summary>Natural language prompt that defines the workflow.</summary>
    public required string Prompt { get; set; }

    /// <summary>Cron expression for scheduled execution. Null means manual-only. Mutually exclusive with <see cref="ScheduleOnce"/>.</summary>
    public string? Schedule { get; set; }

    /// <summary>One-time execution datetime (ISO 8601). Mutually exclusive with <see cref="Schedule"/>.</summary>
    public DateTimeOffset? ScheduleOnce { get; set; }

    /// <summary>Whether this task is active for scheduled execution.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Execution guardrails for this task.</summary>
    public required TaskGuardrails Guardrails { get; set; }

    /// <summary>
    /// ProviderModel name (renamed from <c>PresetName</c> in 2.0). Null falls
    /// back to runner.json default. API key is resolved from PasswordVault
    /// using this name.
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// MCP servers to bootstrap for this task's execution.
    /// Uses Core's McpServerConfig model for stdio/http transport support.
    /// </summary>
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// When true, default MCP servers from runner.json are not included.
    /// Only task-specific <c>McpServers</c> are bootstrapped.
    /// </summary>
    public bool ExcludeDefaultServers { get; set; }

    /// <summary>
    /// Outbox channel name for sending results. Null = no notification.
    /// </summary>
    public string? OutputChannel { get; set; }

    /// <summary>Timestamp when this task was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp when this task was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Guardrail constraints for task execution.
/// </summary>
public sealed class TaskGuardrails
{
    /// <summary>
    /// Maximum number of LLM interaction rounds. Default 20 covers a typical
    /// search + summarize + send workflow (2-3 searches, 1-2 fetches, optional
    /// JS calc, final tool call). Raise for multi-step research, lower for
    /// single-tool actions.
    /// </summary>
    public int MaxRounds { get; set; } = 20;

    /// <summary>Execution timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Allowlist of MCP tool names the LLM may invoke.
    /// Null means all discovered tools are permitted.
    /// An explicit empty list means no tools (safe tools only).
    /// </summary>
    public List<string>? AllowedTools { get; set; }
}
