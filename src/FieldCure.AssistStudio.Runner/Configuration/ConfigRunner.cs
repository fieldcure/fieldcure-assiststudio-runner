using System.Text;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Models;

namespace FieldCure.AssistStudio.Runner.Configuration;

/// <summary>
/// Handles CLI config subcommands: init, set-credential, get-credential.
/// </summary>
public static class ConfigRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "init" => RunInit(args[1..]),
            "set-credential" => RunSetCredential(args[1..]),
            "get-credential" => RunGetCredential(args[1..]),
            _ => PrintUsage(),
        };
    }

    /// <summary>
    /// Creates runner.json with optional preset configuration.
    /// <code>
    /// assiststudio-runner config init [--preset Name --provider Type --model Id] [--if-missing]
    /// </code>
    /// </summary>
    static int RunInit(string[] args)
    {
        var parsed = ParseArgs(args, "preset", "provider", "model", "if-missing");
        var presetName = parsed.GetValueOrDefault("preset");
        var providerType = parsed.GetValueOrDefault("provider");
        var modelId = parsed.GetValueOrDefault("model");
        var ifMissing = parsed.ContainsKey("if-missing");

        var configPath = Path.Combine(RunnerConfig.GetDefaultDataDirectory(), "runner.json");

        // --if-missing: skip if runner.json already exists
        if (ifMissing && File.Exists(configPath))
        {
            Console.Error.WriteLine($"Exists: {configPath} (skipped)");
            return 0;
        }

        RunnerConfig config;

        if (!string.IsNullOrEmpty(presetName) && !string.IsNullOrEmpty(providerType))
        {
            // Create config from provided preset info
            config = new RunnerConfig
            {
                DefaultPresetName = presetName,
                Presets = new()
                {
                    [presetName] = new PresetConfig
                    {
                        ProviderType = providerType,
                        ModelId = modelId,
                    }
                },
            };
        }
        else
        {
            // Default template
            config = new RunnerConfig
            {
                DefaultPresetName = "Claude",
                Presets = new()
                {
                    ["Claude"] = new PresetConfig
                    {
                        ProviderType = "Claude",
                        ModelId = "claude-sonnet-4-20250514",
                    }
                },
            };
        }

        config.Save();
        Console.Error.WriteLine($"Created: {configPath}");
        return 0;
    }

    static int RunSetCredential(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: assiststudio-runner config set-credential <key> <value>");
            return 1;
        }

        var key = args[0];
        var value = args[1];

        var service = new CredentialService();
        if (key.StartsWith("McpEnv_"))
        {
            // Parse McpEnv_{serverId}_{varName}
            var parts = key.Split('_', 3);
            if (parts.Length < 3)
            {
                Console.Error.WriteLine("Invalid McpEnv key format. Expected: McpEnv_{serverId}_{key}");
                return 1;
            }
            service.SetMcpEnvVar(parts[1], parts[2], value);
        }
        else
        {
            service.SetApiKey(key, value);
        }

        Console.Error.WriteLine($"Credential '{key}' stored successfully.");
        return 0;
    }

    static int RunGetCredential(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: assiststudio-runner config get-credential <key>");
            return 1;
        }

        var key = args[0];
        var service = new CredentialService();

        string? value;
        if (key.StartsWith("McpEnv_"))
        {
            var parts = key.Split('_', 3);
            if (parts.Length < 3)
            {
                Console.Error.WriteLine("Invalid McpEnv key format. Expected: McpEnv_{serverId}_{key}");
                return 1;
            }
            value = service.GetMcpEnvVar(parts[1], parts[2]);
        }
        else
        {
            value = service.GetApiKey(key);
        }

        if (value is null)
        {
            Console.Error.WriteLine($"Credential '{key}' not found.");
            return 1;
        }

        // Mask the value for display
        var masked = value.Length > 8
            ? value[..4] + new string('*', value.Length - 8) + value[^4..]
            : new string('*', value.Length);

        Console.Error.WriteLine($"{key}: {masked}");
        return 0;
    }

    static int PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  assiststudio-runner config init [options]               Create runner.json");
        Console.Error.WriteLine("    --preset <name>        Preset name (e.g., \"Claude\")");
        Console.Error.WriteLine("    --provider <type>      Provider type (Claude, OpenAI, Gemini, Groq, Ollama)");
        Console.Error.WriteLine("    --model <id>           Model identifier");
        Console.Error.WriteLine("    --if-missing           Skip if runner.json already exists");
        Console.Error.WriteLine("  assiststudio-runner config set-credential <key> <val>   Store a credential");
        Console.Error.WriteLine("  assiststudio-runner config get-credential <key>         Retrieve a credential (masked)");
        return 1;
    }

    /// <summary>
    /// Simple argument parser for --key value pairs and --flag switches.
    /// </summary>
    static Dictionary<string, string> ParseArgs(string[] args, params string[] knownKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;

            var key = args[i][2..];
            if (knownKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                // Check if it's a flag (no value) or key-value pair
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    result[key] = args[++i];
                }
                else
                {
                    result[key] = "true"; // flag
                }
            }
        }
        return result;
    }
}
