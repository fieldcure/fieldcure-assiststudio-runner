using System.Text.Json;
using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FieldCure.AssistStudio.Runner.Tests;

/// <summary>
/// Integration tests that start the Runner as an MCP server (stdio)
/// and exercise all 7 tools via the MCP client SDK.
/// </summary>
[TestClass]
public class McpIntegrationTests
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Temp directory for test isolation — cleaned up in ClassCleanup.</summary>
    static string? _testDataDir;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);
    }

    [ClassCleanup]
    public static void ClassClean()
    {
        if (_testDataDir is not null && Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    static async Task<McpClient> CreateClientAsync()
    {
        // Find the built exe — adjust if running from different directory
        var solutionDir = FindSolutionDir();
        var exe = Path.Combine(solutionDir, "src", "FieldCure.AssistStudio.Runner",
            "bin", "Release", "net8.0", "assiststudio-runner.exe");

        // Fallback: use dotnet run
        string command;
        List<string>? args;
        if (File.Exists(exe))
        {
            command = exe;
            args = ["serve"];
        }
        else
        {
            command = "dotnet";
            args = ["run", "--project",
                Path.Combine(solutionDir, "src", "FieldCure.AssistStudio.Runner"),
                "--", "serve"];
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = args,
            Name = "runner-test",
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["RUNNER_DATA_DIR"] = _testDataDir!,
            },
        });

        return await McpClient.CreateAsync(transport);
    }

    static string FindSolutionDir()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0)
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not find solution directory");
    }

    [TestMethod]
    public async Task ServerInitializes_And_ListsTools()
    {
        await using var client = await CreateClientAsync();

        var tools = await client.ListToolsAsync();
        Assert.AreEqual(7, tools.Count, $"Expected 7 tools, got {tools.Count}");

        var toolNames = tools.Select(t => t.Name).OrderBy(n => n).ToList();
        CollectionAssert.Contains(toolNames, "create_task");
        CollectionAssert.Contains(toolNames, "update_task");
        CollectionAssert.Contains(toolNames, "delete_task");
        CollectionAssert.Contains(toolNames, "list_tasks");
        CollectionAssert.Contains(toolNames, "run_task");
        CollectionAssert.Contains(toolNames, "get_task_history");
        CollectionAssert.Contains(toolNames, "get_execution_status");
    }

    [TestMethod]
    public async Task FullCrudLifecycle()
    {
        await using var client = await CreateClientAsync();

        // 1. list_tasks — empty
        var result = await CallToolAsync(client, "list_tasks", new { });
        var listData = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual(0, listData.GetProperty("total_count").GetInt32());

        // 2. create_task
        result = await CallToolAsync(client, "create_task", new
        {
            name = "Integration Test Task",
            prompt = "Say hello",
            mcp_servers = "[]",
            description = "test",
            max_rounds = 3,
        });
        var createData = JsonDocument.Parse(result).RootElement;
        Assert.IsTrue(createData.GetProperty("success").GetBoolean());
        var taskId = createData.GetProperty("task_id").GetString()!;

        // 3. list_tasks — 1 task
        result = await CallToolAsync(client, "list_tasks", new { });
        listData = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual(1, listData.GetProperty("total_count").GetInt32());

        // 4. update_task
        result = await CallToolAsync(client, "update_task", new
        {
            task_id = taskId,
            name = "Updated Task",
            max_rounds = 8,
        });
        var updateData = JsonDocument.Parse(result).RootElement;
        Assert.IsTrue(updateData.GetProperty("success").GetBoolean());

        // 5. get_task_history — empty
        result = await CallToolAsync(client, "get_task_history", new
        {
            task_id = taskId,
        });
        var histData = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("Updated Task", histData.GetProperty("task_name").GetString());
        Assert.AreEqual(0, histData.GetProperty("total_count").GetInt32());

        // 6. get_execution_status — not found
        result = await CallToolAsync(client, "get_execution_status", new
        {
            execution_id = "nonexistent",
        });
        var statusData = JsonDocument.Parse(result).RootElement;
        Assert.IsFalse(statusData.GetProperty("success").GetBoolean());

        // 7. delete_task
        result = await CallToolAsync(client, "delete_task", new
        {
            task_id = taskId,
        });
        var deleteData = JsonDocument.Parse(result).RootElement;
        Assert.IsTrue(deleteData.GetProperty("deleted").GetBoolean());

        // 8. verify empty
        result = await CallToolAsync(client, "list_tasks", new { });
        listData = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual(0, listData.GetProperty("total_count").GetInt32());
    }

    static async Task<string> CallToolAsync(McpClient client, string toolName, object arguments)
    {
        var tools = await client.ListToolsAsync();
        var tool = tools.First(t => t.Name == toolName);

        var argsJson = JsonSerializer.Serialize(arguments, JsonOpts);
        var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson, JsonOpts) ?? new();

        var result = await tool.CallAsync(argsDict);

        if (result.Content is { Count: > 0 } content)
        {
            var texts = content
                .Where(c => c is TextContentBlock)
                .Select(c => ((TextContentBlock)c).Text);
            return string.Join("\n", texts);
        }
        return "{}";
    }
}
