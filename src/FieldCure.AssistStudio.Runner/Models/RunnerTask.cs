using FieldCure.AssistStudio.Models;

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

    /// <summary>Cron expression for scheduled execution. Null means manual-only.</summary>
    public string? Schedule { get; set; }

    /// <summary>Whether this task is active for scheduled execution.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Execution guardrails for this task.</summary>
    public required TaskGuardrails Guardrails { get; set; }

    /// <summary>
    /// ProviderPreset name. Null falls back to runner.json default.
    /// API key is resolved from PasswordVault using this name.
    /// </summary>
    public string? PresetName { get; set; }

    /// <summary>
    /// MCP servers to bootstrap for this task's execution.
    /// Uses Core's McpServerConfig model for stdio/http transport support.
    /// </summary>
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// Outbox channel name for sending results. Null = no notification.
    /// </summary>
    public string? OutputChannel { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Guardrail constraints for task execution.
/// </summary>
public sealed class TaskGuardrails
{
    /// <summary>Maximum number of LLM interaction rounds.</summary>
    public int MaxRounds { get; set; } = 10;

    /// <summary>Execution timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Allowlist of MCP tool names the LLM may invoke.
    /// Null means no tools permitted (safety-first default).
    /// </summary>
    public List<string>? AllowedTools { get; set; }
}
