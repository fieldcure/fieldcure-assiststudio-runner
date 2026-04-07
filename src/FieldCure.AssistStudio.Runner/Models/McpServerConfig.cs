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
    /// Fills in missing or invalid commands for Stdio servers using auto-detected installed servers.
    /// Replaces the command when it is empty or points to a non-existent file.
    /// </summary>
    public static void ResolveCommands(List<McpServerConfig> servers)
    {
        var needsResolve = servers.Any(s =>
            s.TransportType == McpTransportType.Stdio && !IsCommandValid(s.Command));
        if (!needsResolve) return;

        var detected = RunnerConfig.DetectInstalledServers()
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            if (server.TransportType == McpTransportType.Stdio
                && !IsCommandValid(server.Command)
                && detected.TryGetValue(server.Name, out var entry))
            {
                server.Command = entry.Command;
                server.Arguments = [.. entry.Args];
            }
        }
    }

    /// <summary>
    /// Returns true if the command is non-empty and the file exists (for absolute paths).
    /// Short command names (e.g. on PATH) are assumed valid.
    /// </summary>
    private static bool IsCommandValid(string? command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        if (Path.IsPathFullyQualified(command)) return File.Exists(command);
        return true; // relative or bare command name — assume on PATH
    }
}
