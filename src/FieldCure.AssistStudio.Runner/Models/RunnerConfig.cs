using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldCure.AssistStudio.Models;
using Windows.Security.Credentials;

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
        DataDirectory ?? GetDefaultDataDirectory();

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
    /// </summary>
    static readonly (string Type, string Model)[] KnownProviders =
    [
        ("Claude", "claude-sonnet-4-20250514"),
        ("OpenAI", "gpt-4o"),
        ("Gemini", "gemini-2.0-flash"),
        ("Groq", "llama-3.3-70b-versatile"),
    ];

    /// <summary>
    /// Builds a config by scanning PasswordVault for known provider API keys.
    /// Creates presets for each provider with a stored key, plus Ollama (no key required).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static RunnerConfig BuildFromVault()
    {
        var config = new RunnerConfig();

        PasswordVault vault;
        IReadOnlyList<PasswordCredential> credentials;
        try
        {
            vault = new PasswordVault();
            credentials = vault.FindAllByResource("FieldCure.AssistStudio");
        }
        catch
        {
            // No credentials stored at all
            return config;
        }

        var userNames = new HashSet<string>(credentials.Select(c => c.UserName));

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
