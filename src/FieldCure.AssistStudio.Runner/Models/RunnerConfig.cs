using FieldCure.Ai.Providers.Models;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Execution;
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
    /// <para>
    /// 2.0 is a hard cutover from "preset" to "model" terminology — pre-2.0
    /// runner.json files (with <c>defaultPresetName</c> / <c>presets</c>) are
    /// not migrated. Delete the file before upgrading and let
    /// <see cref="BuildFromVault"/> regenerate it.
    /// </para>
    /// <para>
    /// 2.0.1 retires the <c>%LOCALAPPDATA%\FieldCure\AssistStudio\tools\</c>
    /// install scheme: any legacy absolute-path command in <c>defaultMcpServers</c>
    /// pointing into that folder is rewritten on load to the equivalent <c>dnx</c>
    /// command for known stateless servers, and the migrated config is saved
    /// back so the file on disk reflects the new shape.
    /// </para>
    /// </remarks>
    public static RunnerConfig Load(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? GetDefaultDataDirectory();
        var path = Path.Combine(dir, "runner.json");

        if (!File.Exists(path))
            return new RunnerConfig();

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<RunnerConfig>(json, JsonOptions) ?? new RunnerConfig();

        if (MigrateLegacyToolPathCommands(config))
        {
            try
            {
                config.Save(dir);
            }
            catch (Exception)
            {
                // Migration is best-effort; in-memory config is already migrated
                // and will work for this session even if the durable write fails.
            }
        }

        return config;
    }

    /// <summary>
    /// Rewrites entries in <see cref="DefaultMcpServers"/> whose <see cref="McpServerEntry.Command"/>
    /// points at the retired <c>%LOCALAPPDATA%\FieldCure\AssistStudio\tools\</c>
    /// folder so they spawn through <c>dnx</c> instead. Only known stateless
    /// servers (essentials, outbox) are migrated — unknown entries are left
    /// untouched. Returns <see langword="true"/> when any entry changed.
    /// </summary>
    static bool MigrateLegacyToolPathCommands(RunnerConfig config)
    {
        if (config.DefaultMcpServers.Count == 0)
            return false;

        var legacyToolDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FieldCure", "AssistStudio", "tools") + Path.DirectorySeparatorChar;

        var dnxEntries = DetectInstalledServers()
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        if (dnxEntries.Count == 0)
            return false;

        var changed = false;
        foreach (var entry in config.DefaultMcpServers)
        {
            if (string.IsNullOrEmpty(entry.Command))
                continue;
            if (!entry.Command.StartsWith(legacyToolDir, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!dnxEntries.TryGetValue(entry.Name, out var dnxEntry))
                continue;

            entry.Command = dnxEntry.Command;
            entry.Args = [.. dnxEntry.Args];
            changed = true;
        }

        return changed;
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
    /// Stateless MCP servers that Runner can bootstrap on demand without
    /// per-session context (folders, indexes, etc.). Each entry pins a major
    /// version range — bumping the major requires an intentional Runner
    /// release so breaking changes never sneak in mid-cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EnvKeys</c> are resolved at bootstrap by
    /// <see cref="Credentials.ICredentialService.GetMcpEnvVar"/> from the shared
    /// <c>McpEnv_{serverId}_{key}</c> slot — the same slot the host (AssistStudio)
    /// writes to, so keys set in the host are picked up automatically.
    /// </para>
    /// <para>
    /// Servers are spawned via <c>dnx</c> (NuGet's npx-equivalent), eliminating
    /// the earlier <c>%LOCALAPPDATA%\FieldCure\AssistStudio\tools\</c> tool-path
    /// install scheme. dnx caches packages under <c>%USERPROFILE%\.dnx\packages</c>
    /// and resolves them lazily on first invocation.
    /// </para>
    /// </remarks>
    static readonly StatelessServerEntry[] StatelessServers =
    [
        new("essentials", "FieldCure.Mcp.Essentials", "2.*",
            ["SERPER_API_KEY", "TAVILY_API_KEY", "SERPAPI_API_KEY", "WOLFRAM_APPID"]),
        new("outbox",     "FieldCure.Mcp.Outbox",     "2.*", []),
    ];

    /// <summary>
    /// Returns dnx-based <see cref="McpServerEntry"/> definitions for every
    /// stateless server in <see cref="StatelessServers"/>. Detection is purely
    /// PATH-based now — if <c>dnx</c> is on PATH the entry is produced; otherwise
    /// the list is empty (caller logs "no MCP servers configured" and proceeds).
    /// </summary>
    public static List<McpServerEntry> DetectInstalledServers()
    {
        var dnx = DnxResolver.Path;
        if (dnx is null)
            return [];

        var entries = new List<McpServerEntry>(StatelessServers.Length);
        foreach (var server in StatelessServers)
        {
            entries.Add(new McpServerEntry
            {
                Name = server.Name,
                Command = dnx,
                Args = [$"{server.PackageId}@{server.MajorRange}", "--yes"],
                IsBuiltIn = true,
                EnvironmentVariableKeys = server.EnvKeys.Length > 0 ? [.. server.EnvKeys] : null,
            });
        }

        return entries;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> matches a
    /// known stateless server. Used by callers that need to decide whether to
    /// auto-resolve a stale or LLM-provided command for the server.
    /// </summary>
    internal static bool IsKnownStatelessServer(string name)
    {
        foreach (var server in StatelessServers)
        {
            if (server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Definition of a stateless MCP server Runner can auto-bootstrap. Bundles
    /// the package id with its pinned major range so the spawn command line is
    /// stable across NuGet releases (within the major) and explicit at major bumps.
    /// </summary>
    /// <param name="Name">Logical server name used as the merge key in <c>runner.json</c>.</param>
    /// <param name="PackageId">NuGet package id consumed by <c>dnx</c>.</param>
    /// <param name="MajorRange">Pinned major version range (e.g. <c>"2.*"</c>); bump intentionally on major releases.</param>
    /// <param name="EnvKeys">Environment variable keys the server reads at startup.</param>
    sealed record StatelessServerEntry(string Name, string PackageId, string MajorRange, string[] EnvKeys);

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
