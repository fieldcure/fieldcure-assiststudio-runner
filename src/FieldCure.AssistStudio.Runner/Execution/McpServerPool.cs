using System.Text.Json;
using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FieldCure.AssistStudio.Runner.Execution;

/// <summary>
/// Manages lifecycle of MCP server processes for a single task execution.
/// Supports both stdio and HTTP transports via Core's McpServerConfig.
/// </summary>
internal sealed class McpServerPool : IAsyncDisposable
{
    /// <summary>
    /// Tools that are always allowed regardless of the allowlist.
    /// These tools have no side effects and provide context/computation only.
    /// </summary>
    static readonly HashSet<string> SafeTools = ["get_environment", "run_javascript"];

    readonly List<McpClient> _clients = [];
    readonly Dictionary<string, McpToolAdapter> _toolMap = new();
    readonly ILogger _logger;

    internal McpServerPool(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts all configured MCP servers and collects available tools.
    /// </summary>
    public async Task<IReadOnlyList<McpToolAdapter>> BootstrapAsync(
        IReadOnlyList<McpServerConfig> configs,
        IReadOnlyList<string>? allowedTools,
        ICredentialService credentialService)
    {
        var tools = new List<McpToolAdapter>();

        foreach (var config in configs)
        {
            McpClient client;
            try
            {
                client = await CreateClientAsync(config, credentialService);
                _clients.Add(client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bootstrap MCP server '{Name}' ({Id})", config.Name, config.Id);
                throw;
            }

            // Discover tools from this server
            var serverTools = await client.ListToolsAsync();
            _logger.LogDebug("Server '{Name}' provides {Count} tools", config.Name, serverTools.Count);

            foreach (var mcpTool in serverTools)
            {
                var capturedTool = mcpTool; // capture for closure
                var adapter = new McpToolAdapter(
                    capturedTool.Name,
                    capturedTool.Description ?? "",
                    capturedTool.JsonSchema.GetRawText(),
                    async (args, ct) =>
                    {
                        var argsDict = ConvertJsonArguments(args);
                        var result = await capturedTool.CallAsync(argsDict, cancellationToken: ct);
                        return ExtractTextResult(result);
                    })
                {
                    ServerName = config.Name,
                    OverrideRequiresConfirmation = false, // headless = pre-approved
                };

                _toolMap[capturedTool.Name] = adapter;

                // null = all tools allowed; explicit list = filter; empty list = safe tools only
                if (allowedTools is null
                    || allowedTools.Contains(capturedTool.Name)
                    || SafeTools.Contains(capturedTool.Name))
                {
                    tools.Add(adapter);
                }
            }
        }

        _logger.LogInformation("MCP bootstrap complete: {Total} tools discovered, {Allowed} allowed",
            _toolMap.Count, tools.Count);

        return tools;
    }

    /// <summary>
    /// Gets a tool adapter by name (for notification routing, even if not in AllowedTools).
    /// </summary>
    public McpToolAdapter? GetTool(string toolName) =>
        _toolMap.TryGetValue(toolName, out var adapter) ? adapter : null;

    static async Task<McpClient> CreateClientAsync(
        McpServerConfig config, ICredentialService credentialService)
    {
        // Resolve environment variables from PasswordVault
        var envVars = new Dictionary<string, string?>();
        if (config.EnvironmentVariableKeys is { Count: > 0 })
        {
            foreach (var key in config.EnvironmentVariableKeys)
            {
                var value = credentialService.GetMcpEnvVar(config.Id, key);
                if (value is not null)
                    envVars[key] = value;
            }
        }

        IClientTransport transport;

        if (config.TransportType == McpTransportType.Stdio)
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command ?? throw new InvalidOperationException(
                    $"MCP server '{config.Name}' is stdio but has no command."),
                Arguments = config.Arguments,
                EnvironmentVariables = envVars.Count > 0 ? envVars : null,
                Name = config.Name,
            });
        }
        else if (config.TransportType == McpTransportType.Http)
        {
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url ?? throw new InvalidOperationException(
                    $"MCP server '{config.Name}' is HTTP but has no URL.")),
                Name = config.Name,
            });
        }
        else
        {
            throw new InvalidOperationException($"Unsupported transport type: {config.TransportType}");
        }

        return await McpClient.CreateAsync(transport);
    }

    static Dictionary<string, object?> ConvertJsonArguments(JsonElement arguments)
    {
        var argsDict = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
                argsDict[prop.Name] = ConvertJsonElement(prop.Value);
        }
        return argsDict;
    }

    static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
        _ => element.GetRawText(),
    };

    static string ExtractTextResult(CallToolResult result)
    {
        if (result.Content is { Count: > 0 } content)
        {
            var texts = content
                .Where(c => c is TextContentBlock)
                .Select(c => ((TextContentBlock)c).Text);
            return string.Join("\n", texts);
        }
        return result.IsError == true ? """{"error": true}""" : "{}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client");
            }
        }

        _clients.Clear();
        _toolMap.Clear();
    }
}
