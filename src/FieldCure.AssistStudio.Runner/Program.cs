using System.Reflection;
using System.Text;
using FieldCure.AssistStudio.Runner.Configuration;
using FieldCure.AssistStudio.Runner.Credentials;
using FieldCure.AssistStudio.Runner.Execution;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Scheduling;
using FieldCure.AssistStudio.Runner.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0)
{
    Console.OutputEncoding = Encoding.UTF8;

    return args[0].ToLowerInvariant() switch
    {
        "exec" when args.Length >= 2 => await RunExecAsync(args[1]),
        "config" => ConfigRunner.Run(args[1..]),
        "serve" => await RunServeAsync(),
        _ => PrintUsage(),
    };
}

// Default: serve mode
return await RunServeAsync();

// ── Serve Mode ──────────────────────────────────────────────────────────────

async Task<int> RunServeAsync()
{
    var config = RunnerConfig.Load();

    // Auto-init: build runner.json from PasswordVault if no presets configured
    if (config.Presets.Count == 0)
    {
        config = RunnerConfig.BuildFromVault();
        config.Save();
    }

    var dataDir = config.GetEffectiveDataDirectory();

    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    // Register services
    builder.Services
        .AddSingleton(config)
        .AddSingleton(new TaskStore(dataDir))
        .AddSingleton<ICredentialService, CredentialService>()
        .AddSingleton<SchedulerService>()
        .AddSingleton<TaskExecutor>()
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "assiststudio-runner",
                Title = "AssistStudio Runner",
                Description = "Headless LLM task runner with scheduling via Windows Task Scheduler",
                Version = typeof(Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "0.0.0",
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();
    await app.RunAsync();
    return 0;
}

// ── Exec Mode ───────────────────────────────────────────────────────────────

async Task<int> RunExecAsync(string taskId)
{
    var config = RunnerConfig.Load();
    var dataDir = config.GetEffectiveDataDirectory();

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
    });

    var logger = loggerFactory.CreateLogger<TaskExecutor>();
    var store = new TaskStore(dataDir);
    var credentialService = new CredentialService();

    var executor = new TaskExecutor(store, config, credentialService, logger);

    try
    {
        var execution = await executor.ExecuteAsync(taskId);

        return execution.Status switch
        {
            ExecutionStatus.Succeeded => 0,
            ExecutionStatus.TimedOut => 2,
            _ => 1,
        };
    }
    catch (TaskNotFoundException)
    {
        Console.Error.WriteLine($"Task '{taskId}' not found.");
        return 3;
    }
    catch (AlreadyRunningException)
    {
        Console.Error.WriteLine($"Task '{taskId}' is already running.");
        return 4;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// ── Usage ───────────────────────────────────────────────────────────────────

static int PrintUsage()
{
    Console.Error.WriteLine("AssistStudio Runner — Headless LLM task automation engine");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  assiststudio-runner [serve]              Start MCP server (stdio)");
    Console.Error.WriteLine("  assiststudio-runner exec <task-id>       Execute a task headlessly");
    Console.Error.WriteLine("  assiststudio-runner config <subcommand>  Manage credentials and configuration");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes (exec mode):");
    Console.Error.WriteLine("  0  Succeeded");
    Console.Error.WriteLine("  1  Failed");
    Console.Error.WriteLine("  2  Timed out");
    Console.Error.WriteLine("  3  Task not found");
    Console.Error.WriteLine("  4  Already running");
    return 1;
}
