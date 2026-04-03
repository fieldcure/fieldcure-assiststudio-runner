using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;

namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Global configuration loaded from runner.json.
/// </summary>
public sealed class RunnerConfig
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Fallback provider preset name when a task doesn't specify one.</summary>
    public string? DefaultPresetName { get; set; }

    /// <summary>Provider preset definitions.</summary>
    public Dictionary<string, PresetConfig> Presets { get; set; } = new();

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
    /// Resolves a preset name to a <see cref="ProviderPreset"/> instance.
    /// Falls back to matching by provider type if exact name match fails.
    /// </summary>
    public ProviderPreset? ResolvePreset(string? presetName)
    {
        if (presetName is null) return null;

        // 1. Exact match by preset name
        if (Presets.TryGetValue(presetName, out var config))
            return ToPreset(presetName, config);

        // 2. Fallback: match by providerType (e.g., "Claude" matches a preset with ProviderType="Claude")
        var byType = Presets.FirstOrDefault(p =>
            p.Value.ProviderType.Equals(presetName, StringComparison.OrdinalIgnoreCase));
        if (byType.Value is not null)
            return ToPreset(byType.Key, byType.Value);

        return null;
    }

    static ProviderPreset ToPreset(string name, PresetConfig config) => new()
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
    /// Users can override model IDs in their runner.json presets.
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
    /// Creates presets for each provider with a stored key, plus Ollama (no key required).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static RunnerConfig BuildFromVault()
    {
        var config = new RunnerConfig();
        var credService = new CredentialService();
        var userNames = new HashSet<string>(credService.EnumerateUserNames());

        foreach (var (type, model) in KnownProviders)
        {
            if (userNames.Contains(type))
            {
                config.Presets[type] = new PresetConfig
                {
                    ProviderType = type,
                    ModelId = model,
                };
                config.DefaultPresetName ??= type;
            }
        }

        // Ollama — no API key required, always available
        config.Presets["Ollama"] = new PresetConfig
        {
            ProviderType = "Ollama",
            ModelId = "llama3.1:latest",
            BaseUrl = "http://localhost:11434",
        };

        // Auto-detect installed Essentials MCP server
        var essentialsExeName = OperatingSystem.IsWindows()
            ? "fieldcure-mcp-essentials.exe"
            : "fieldcure-mcp-essentials";

        // Check global dotnet tool path
        var globalToolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", essentialsExeName);

        // Check AssistStudio's local tool path
        var localToolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FieldCure", "AssistStudio", "tools", essentialsExeName);

        if (File.Exists(globalToolPath) || File.Exists(localToolPath))
        {
            config.DefaultMcpServers.Add(new McpServerEntry
            {
                Name = "essentials",
                Command = "fieldcure-mcp-essentials",
            });
        }

        return config;
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
/// Provider preset configuration stored in runner.json.
/// </summary>
public sealed class PresetConfig
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
    /// Converts to a <see cref="McpServerConfig"/> for MCP client bootstrapping.
    /// </summary>
    public McpServerConfig ToMcpServerConfig() => new()
    {
        Id = $"default_{Name}",
        Name = Name,
        TransportType = McpTransportType.Stdio,
        Command = Command,
        Arguments = Args,
        IsEnabled = true,
    };
}
