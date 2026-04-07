using System.Text.Json.Serialization;

namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Transport type for MCP server connections.
/// </summary>
public enum McpTransportType
{
    /// <summary>Standard I/O transport (child process).</summary>
    Stdio,

    /// <summary>HTTP transport (SSE or Streamable HTTP).</summary>
    Http,
}

/// <summary>
/// MCP server configuration for task execution.
/// Lightweight Runner-local model (no dependency on Core).
/// </summary>
public class McpServerConfig
{
    /// <summary>Short unique identifier for this server.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Display name of the MCP server.</summary>
    public string Name { get; set; } = "";

    /// <summary>Transport type: Stdio or Http.</summary>
    public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;

    /// <summary>Command to launch the server (Stdio only).</summary>
    public string? Command { get; set; }

    /// <summary>Arguments for the command (Stdio only).</summary>
    public List<string>? Arguments { get; set; }

    /// <summary>Server URL (Http only).</summary>
    public string? Url { get; set; }

    /// <summary>Environment variable values for the server process (Stdio only).</summary>
    [JsonIgnore]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>Environment variable key names (serialized to JSON).</summary>
    public List<string>? EnvironmentVariableKeys { get; set; }

    /// <summary>Whether this server is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Resolves commands for Stdio servers using auto-detected installed servers.
    /// Known servers always get the auto-detected path regardless of what the LLM provided.
    /// Unknown servers keep their original command value.
    /// </summary>
    public static void ResolveCommands(List<McpServerConfig> servers)
    {
        var detected = RunnerConfig.DetectInstalledServers()
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        if (detected.Count == 0) return;

        foreach (var server in servers)
        {
            if (server.TransportType == McpTransportType.Stdio
                && detected.TryGetValue(server.Name, out var entry))
            {
                // Always prefer auto-detected command over LLM-provided value
                server.Command = entry.Command;
                server.Arguments = [.. entry.Args];
            }
        }
    }
}
