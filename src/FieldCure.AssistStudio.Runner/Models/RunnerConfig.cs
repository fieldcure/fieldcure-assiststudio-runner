using System.Text.Json;
using System.Text.Json.Serialization;
using FieldCure.AssistStudio.Models;

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
    /// </summary>
    public ProviderPreset? ResolvePreset(string? presetName)
    {
        if (presetName is null) return null;
        if (!Presets.TryGetValue(presetName, out var config)) return null;

        return new ProviderPreset
        {
            Name = presetName,
            ProviderType = config.ProviderType,
            ModelId = config.ModelId ?? "",
            BaseUrl = config.BaseUrl,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
        };
    }

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
