using FieldCure.AssistStudio.Runner.Models;
using FieldCure.AssistStudio.Runner.Storage;

namespace FieldCure.AssistStudio.Runner.Tests;

[TestClass]
public class TaskStoreTests
{
    TaskStore CreateInMemoryStore()
    {
        // Each test gets a unique in-memory database
        var connStr = $"Data Source=InMemory{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        return new TaskStore(connStr, useRawConnectionString: true);
    }

    static RunnerTask CreateSampleTask(string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Name = "Test Task",
        Prompt = "Do something useful",
        Guardrails = new TaskGuardrails
        {
            MaxRounds = 5,
            TimeoutSeconds = 120,
            AllowedTools = ["tool_a", "tool_b"],
        },
        McpServers = [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [TestMethod]
    public async Task InsertAndGet_RoundTrips()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();

        await store.InsertTaskAsync(task);
        var loaded = await store.GetTaskAsync(task.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(task.Name, loaded.Name);
        Assert.AreEqual(task.Prompt, loaded.Prompt);
        Assert.AreEqual(5, loaded.Guardrails.MaxRounds);
        Assert.AreEqual(2, loaded.Guardrails.AllowedTools!.Count);
    }

    [TestMethod]
    public async Task GetTask_NotFound_ReturnsNull()
    {
        using var store = CreateInMemoryStore();
        var result = await store.GetTaskAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task UpdateTask_ChangesFields()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        await store.InsertTaskAsync(task);

        task.Name = "Updated Name";
        task.Schedule = "0 9 * * *";
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateTaskAsync(task);

        var loaded = await store.GetTaskAsync(task.Id);
        Assert.AreEqual("Updated Name", loaded!.Name);
        Assert.AreEqual("0 9 * * *", loaded.Schedule);
    }

    [TestMethod]
    public async Task DeleteTask_CascadesExecutions()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        await store.InsertTaskAsync(task);

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertExecutionAsync(execution);

        var (removed, logs) = await store.DeleteTaskAsync(task.Id);
        Assert.AreEqual(1, removed);

        var loadedTask = await store.GetTaskAsync(task.Id);
        Assert.IsNull(loadedTask);
    }

    [TestMethod]
    public async Task GetAllTasks_FilterEnabled()
    {
        using var store = CreateInMemoryStore();

        var t1 = CreateSampleTask();
        t1.IsEnabled = true;
        await store.InsertTaskAsync(t1);

        var t2 = CreateSampleTask();
        t2.IsEnabled = false;
        await store.InsertTaskAsync(t2);

        var enabled = await store.GetAllTasksAsync(statusFilter: "enabled");
        Assert.AreEqual(1, enabled.Count);
        Assert.IsTrue(enabled[0].IsEnabled);

        var disabled = await store.GetAllTasksAsync(statusFilter: "disabled");
        Assert.AreEqual(1, disabled.Count);
        Assert.IsFalse(disabled[0].IsEnabled);

        var all = await store.GetAllTasksAsync(statusFilter: "all");
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public async Task Execution_CRUD()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        await store.InsertTaskAsync(task);

        var exec = new TaskExecution
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertExecutionAsync(exec);

        Assert.IsTrue(await store.HasRunningExecutionAsync(task.Id));

        exec.Status = ExecutionStatus.Succeeded;
        exec.CompletedAt = DateTimeOffset.UtcNow;
        exec.ResultSummary = "All good";
        await store.UpdateExecutionAsync(exec);

        Assert.IsFalse(await store.HasRunningExecutionAsync(task.Id));

        var loaded = await store.GetExecutionAsync(exec.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(ExecutionStatus.Succeeded, loaded.Status);
        Assert.AreEqual("All good", loaded.ResultSummary);
    }

    [TestMethod]
    public async Task GetExecutions_WithLimit()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        await store.InsertTaskAsync(task);

        for (var i = 0; i < 5; i++)
        {
            await store.InsertExecutionAsync(new TaskExecution
            {
                Id = Guid.NewGuid().ToString(),
                TaskId = task.Id,
                Status = ExecutionStatus.Succeeded,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        }

        var limited = await store.GetExecutionsAsync(task.Id, limit: 3);
        Assert.AreEqual(3, limited.Count);

        var total = await store.GetExecutionCountAsync(task.Id);
        Assert.AreEqual(5, total);
    }

    [TestMethod]
    public async Task NullAllowedTools_MeansNoTools()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        task.Guardrails.AllowedTools = null;
        await store.InsertTaskAsync(task);

        var loaded = await store.GetTaskAsync(task.Id);
        Assert.IsNull(loaded!.Guardrails.AllowedTools);
    }

    [TestMethod]
    public async Task ScheduleOnce_RoundTrips()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        task.ScheduleOnce = new DateTimeOffset(2026, 4, 7, 15, 21, 0, TimeSpan.FromHours(9));
        await store.InsertTaskAsync(task);

        var loaded = await store.GetTaskAsync(task.Id);
        Assert.IsNotNull(loaded);
        Assert.IsNotNull(loaded.ScheduleOnce);
        Assert.IsNull(loaded.Schedule);
        Assert.AreEqual(2026, loaded.ScheduleOnce.Value.Year);
        Assert.AreEqual(4, loaded.ScheduleOnce.Value.Month);
        Assert.AreEqual(7, loaded.ScheduleOnce.Value.Day);
    }

    [TestMethod]
    public async Task ScheduleOnce_Null_RoundTrips()
    {
        using var store = CreateInMemoryStore();
        var task = CreateSampleTask();
        task.ScheduleOnce = null;
        await store.InsertTaskAsync(task);

        var loaded = await store.GetTaskAsync(task.Id);
        Assert.IsNotNull(loaded);
        Assert.IsNull(loaded.ScheduleOnce);
    }

    [TestMethod]
    public async Task GetAllTasks_FilterHasSchedule_IncludesOnce()
    {
        using var store = CreateInMemoryStore();

        var cronTask = CreateSampleTask();
        cronTask.Schedule = "0 9 * * *";
        await store.InsertTaskAsync(cronTask);

        var onceTask = CreateSampleTask();
        onceTask.ScheduleOnce = DateTimeOffset.UtcNow.AddHours(1);
        await store.InsertTaskAsync(onceTask);

        var manualTask = CreateSampleTask();
        await store.InsertTaskAsync(manualTask);

        var scheduled = await store.GetAllTasksAsync(hasSchedule: true);
        Assert.AreEqual(2, scheduled.Count);

        var unscheduled = await store.GetAllTasksAsync(hasSchedule: false);
        Assert.AreEqual(1, unscheduled.Count);
    }
}
