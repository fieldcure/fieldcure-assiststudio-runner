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
            "init" => RunInit(),
            "set-credential" => RunSetCredential(args[1..]),
            "get-credential" => RunGetCredential(args[1..]),
            _ => PrintUsage(),
        };
    }

    static int RunInit()
    {
        var config = new RunnerConfig
        {
            DefaultPresetName = "Claude Sonnet",
            Presets = new()
            {
                ["Claude Sonnet"] = new PresetConfig
                {
                    ProviderType = "Claude",
                    ModelId = "claude-sonnet-4-20250514",
                }
            },
            FallbackChannel = "runner-alerts",
        };

        config.Save();
        var path = Path.Combine(config.GetEffectiveDataDirectory(), "runner.json");
        Console.Error.WriteLine($"Created: {path}");
        Console.Error.WriteLine("Next: set your API key with:");
        Console.Error.WriteLine("  assiststudio-runner config set-credential \"Claude Sonnet\" <your-api-key>");
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
        Console.Error.WriteLine("  assiststudio-runner config init                        Create runner.json template");
        Console.Error.WriteLine("  assiststudio-runner config set-credential <key> <val>  Store a credential");
        Console.Error.WriteLine("  assiststudio-runner config get-credential <key>        Retrieve a credential (masked)");
        return 1;
    }
}
