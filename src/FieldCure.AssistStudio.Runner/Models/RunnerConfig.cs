using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Global configuration loaded from runner.json.
/// </summary>
public sealed class RunnerConfig
{
    /// <summary>Shared JSON serializer options for runner.json read/write.</summary>
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Fallback provider model name when a task doesn't specify one.</summary>
    public string? DefaultModelName { get; set; }

    /// <summary>Provider model definitions.</summary>
    public Dictionary<string, ModelConfig> Models { get; set; } = new();

    /// <summary>Runner executable path override. Null = use PATH.</summary>
    public string? ToolPath { get; set; }

    /// <summary>Data directory override. Null = %LOCALAPPDATA%/FieldCure/AssistStudio/Runner/.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Outbox channel for failure alerts.</summary>
    public string? FallbackChannel { get; set; }

    /// <summary>Number of days to retain execution log files. 0 = unlimited.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>LLM API retry policy.</summary>
    public RetryConfig Retry { get; set; } = new();

    /// <summary>
    /// MCP servers that are automatically bootstrapped for every task execution.
    /// Task-specific <c>mcp_servers</c> are merged on top of these defaults.
    /// </summary>
    public List<McpServerEntry> DefaultMcpServers { get; set; } = [];

    /// <summary>
    /// Resolves a model name to a <see cref="ProviderModel"/> instance.
    /// Falls back to matching by provider type if exact name match fails.
    /// </summary>
    public ProviderModel? ResolveModel(string? modelName)
    {
        if (modelName is null) return null;

        // 1. Exact match by model name
        if (Models.TryGetValue(modelName, out var config))
            return ToProviderModel(modelName, config);

        // 2. Fallback: match by providerType (e.g., "Claude" matches a model with ProviderType="Claude")
        var byType = Models.FirstOrDefault(p =>
            p.Value.ProviderType.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        if (byType.Value is not null)
            return ToProviderModel(byType.Key, byType.Value);

        return null;
    }

    /// <summary>Converts a <see cref="ModelConfig"/> to a <see cref="ProviderModel"/> instance.</summary>
    static ProviderModel ToProviderModel(string name, ModelConfig config) => new()
    {
        Name = name,
        ProviderType = config.ProviderType,
        ModelId = config.ModelId ?? "",
        BaseUrl = config.BaseUrl,
        Temperature = config.Temperature,
        MaxTokens = config.MaxTokens,
    };

    /// <summary>
    /// Returns the default data directory path.
    /// </summary>
    public static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "FieldCure", "AssistStudio", "Runner");
    }

    /// <summary>
    /// Resolves the effective data directory (config override or default).
    /// </summary>
    public string GetEffectiveDataDirectory() =>
        DataDirectory
        ?? Environment.GetEnvironmentVariable("RUNNER_DATA_DIR")
        ?? GetDefaultDataDirectory();

    /// <summary>
    /// Loads configuration from the specified directory's runner.json.
    /// Returns default config if the file doesn't exist.
    /// </summary>
    /// <remarks>
    /// 2.0 is a hard cutover from "preset" to "model" terminology — pre-2.0
    /// runner.json files (with <c>defaultPresetName</c> / <c>presets</c>) are
    /// not migrated. Delete the file before upgrading and let
    /// <see cref="BuildFromVault"/> regenerate it.
    /// </remarks>
    public static RunnerConfig Load(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? GetDefaultDataDirectory();
        var path = Path.Combine(dir, "runner.json");

        if (!File.Exists(path))
            return new RunnerConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RunnerConfig>(json, JsonOptions) ?? new RunnerConfig();
    }

    /// <summary>
    /// Known cloud providers with default model IDs.
    /// These are used only for auto-config when no runner.json exists.
    /// Users can override model IDs in their runner.json models.
    /// </summary>
    /// <remarks>
    /// Last updated: 2025-05. Update these when major new models are released.
    /// </remarks>
    static readonly (string Type, string Model)[] KnownProviders =
    [
        ("Claude", "claude-sonnet-4-20250514"),
        ("OpenAI", "gpt-4o"),
        ("Gemini", "gemini-2.0-flash"),
        ("Groq", "llama-3.3-70b-versatile"),
    ];

    /// <summary>
    /// Builds a config by scanning Windows Credential Manager for known provider API keys.
    /// Creates models for each provider with a stored key, plus Ollama (no key required).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static RunnerConfig BuildFromVault()
    {
        var config = new RunnerConfig();
        var credService = new CredentialService();
        var userNames = new HashSet<string>(credService.EnumerateUserNames());

        foreach (var (type, modelId) in KnownProviders)
        {
            if (userNames.Contains(type))
            {
                config.Models[type] = new ModelConfig
                {
                    ProviderType = type,
                    ModelId = modelId,
                };
                config.DefaultModelName ??= type;
            }
        }

        // Ollama — no API key required, always available
        config.Models["Ollama"] = new ModelConfig
        {
            ProviderType = "Ollama",
            ModelId = "llama3.1:latest",
            BaseUrl = "http://localhost:11434",
        };

        config.DefaultMcpServers.AddRange(DetectInstalledServers());

        return config;
    }

    /// <summary>
    /// Stateless MCP servers that can be auto-detected and bootstrapped
    /// without per-session context (folders, indexes, etc.).
    /// </summary>
    /// <summary>
    /// Environment variable keys that each stateless server consumes (ADR-001).
    /// Resolved at bootstrap time by <see cref="Credentials.ICredentialService.GetMcpEnvVar"/>
    /// using the shared <c>McpEnv_{serverId}_{key}</c> slot — the same slot the host
    /// (AssistStudio) writes to, so keys set in the host are picked up automatically.
    /// </summary>
    static readonly (string Name, string Command, string[] Args, string[] EnvKeys)[] StatelessServers =
    [
        ("essentials", "fieldcure-mcp-essentials", [], new[]
        {
            "SERPER_API_KEY",
            "TAVILY_API_KEY",
            "SERPAPI_API_KEY",
            "WOLFRAM_APPID",
        }),
        ("outbox", "fieldcure-mcp-outbox", [], Array.Empty<string>()),
    ];

    /// <summary>
    /// Detects installed stateless dotnet tool MCP servers and returns their entries.
    /// Checks both global (<c>~/.dotnet/tools/</c>) and local
    /// (<c>%LOCALAPPDATA%/FieldCure/AssistStudio/tools/</c>) install paths.
    /// </summary>
    public static List<McpServerEntry> DetectInstalledServers()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";

        var globalToolDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");
        var localToolDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FieldCure", "AssistStudio", "tools");

        var entries = new List<McpServerEntry>();

        foreach (var (name, command, args, envKeys) in StatelessServers)
        {
            var exeName = command + ext;
            var localPath = Path.Combine(localToolDir, exeName);
            var globalPath = Path.Combine(globalToolDir, exeName);

            // Resolve to full path so exec mode works without PATH
            string? resolvedCommand = File.Exists(localPath) ? localPath
                : File.Exists(globalPath) ? globalPath
                : null;

            if (resolvedCommand is not null)
            {
                entries.Add(new McpServerEntry
                {
                    Name = name,
                    Command = resolvedCommand,
                    Args = [.. args],
                    IsBuiltIn = true,
                    EnvironmentVariableKeys = envKeys.Length > 0 ? [.. envKeys] : null,
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Saves configuration to the specified directory's runner.json.
    /// </summary>
    public void Save(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? GetEffectiveDataDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "runner.json");
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}

/// <summary>
/// Provider model configuration stored in runner.json.
/// Renamed from <c>PresetConfig</c> in 2.0 to align with the
/// upstream <c>FieldCure.Ai.Providers</c> rename of <c>ProviderPreset</c>
/// to <c>ProviderModel</c>.
/// </summary>
public sealed class ModelConfig
{
    /// <summary>Provider type: "Claude", "OpenAI", "Gemini", "Ollama", "Groq".</summary>
    public string ProviderType { get; set; } = "Claude";

    /// <summary>Model identifier.</summary>
    public string? ModelId { get; set; }

    /// <summary>Custom base URL for compatible endpoints.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Maximum response tokens.</summary>
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>
/// LLM API retry policy configuration.
/// </summary>
public sealed class RetryConfig
{
    /// <summary>Maximum retry attempts.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Initial delay in milliseconds before first retry.</summary>
    public int InitialDelayMs { get; set; } = 1000;

    /// <summary>Multiplier for exponential backoff between retries.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;
}

/// <summary>
/// Lightweight MCP server definition for runner.json configuration.
/// </summary>
public sealed class McpServerEntry
{
    /// <summary>Server display name (used as merge key).</summary>
    public required string Name { get; set; }

    /// <summary>Executable command (e.g., dotnet tool name).</summary>
    public required string Command { get; set; }

    /// <summary>Command arguments.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Additional environment variables.</summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    /// Environment variable key names the server consumes. Values are resolved from
    /// the shared <c>McpEnv_{serverId}_{key}</c> credential slot at bootstrap (ADR-001).
    /// </summary>
    public List<string>? EnvironmentVariableKeys { get; set; }

    /// <summary>
    /// Whether this entry was auto-detected as a built-in stateless server (Essentials, Outbox).
    /// Not serialized — set by <see cref="RunnerConfig.DetectInstalledServers"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Converts to a <see cref="McpServerConfig"/> for MCP client bootstrapping.
    /// Built-in servers use the <c>builtin_{Name}</c> id so the credential slot
    /// (<c>McpEnv_builtin_{Name}_{key}</c>) is shared with the AssistStudio host.
    /// </summary>
    public McpServerConfig ToMcpServerConfig() => new()
    {
        Id = IsBuiltIn ? $"builtin_{Name}" : Name,
        Name = Name,
        TransportType = McpTransportType.Stdio,
        Command = Command,
        Arguments = Args,
        IsEnabled = true,
        EnvironmentVariableKeys = EnvironmentVariableKeys is { Count: > 0 } ? [.. EnvironmentVariableKeys] : null,
    };
}
