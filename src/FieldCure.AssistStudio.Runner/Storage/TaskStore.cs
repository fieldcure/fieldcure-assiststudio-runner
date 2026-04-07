using FieldCure.AssistStudio.Runner.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.AssistStudio.Runner.Storage;

/// <summary>
/// SQLite-backed storage for Runner tasks and execution history.
/// Uses WAL mode for concurrent serve + exec access.
/// </summary>
public sealed class TaskStore : IDisposable
{
    /// <summary>Shared JSON serialization options for task and execution data.</summary>
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>SQLite connection string for the runner database.</summary>
    readonly string _connectionString;

    /// <summary>Keep-alive connection to prevent in-memory databases from being disposed.</summary>
    SqliteConnection? _keepAlive;

    /// <summary>Initializes a new <see cref="TaskStore"/> using a file-based database in the given directory.</summary>
    public TaskStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "runner.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    /// <summary>
    /// Creates a TaskStore with an explicit connection string (for testing with in-memory DBs).
    /// Pass <paramref name="useRawConnectionString"/> as true to use the string directly.
    /// </summary>
    internal TaskStore(string connectionString, bool useRawConnectionString = false)
    {
        _connectionString = connectionString;
        // For in-memory databases, keep one connection open to prevent data loss
        if (connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
        }
        Initialize();
    }

    /// <summary>Creates database tables and applies schema migrations.</summary>
    void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS Tasks (
                Id              TEXT PRIMARY KEY,
                Name            TEXT NOT NULL,
                Description     TEXT,
                Prompt          TEXT NOT NULL,
                Schedule        TEXT,
                IsEnabled       INTEGER NOT NULL DEFAULT 1,
                MaxRounds       INTEGER NOT NULL DEFAULT 10,
                TimeoutSeconds  INTEGER NOT NULL DEFAULT 300,
                AllowedTools    TEXT,
                PresetName      TEXT,
                McpServers      TEXT NOT NULL,
                OutputChannel   TEXT,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS TaskExecutions (
                Id                 TEXT PRIMARY KEY,
                TaskId             TEXT NOT NULL REFERENCES Tasks(Id) ON DELETE CASCADE,
                Status             TEXT NOT NULL,
                StartedAt          TEXT NOT NULL,
                CompletedAt        TEXT,
                RoundsExecuted     INTEGER NOT NULL DEFAULT 0,
                ResultSummary      TEXT,
                ErrorMessage       TEXT,
                NotificationStatus TEXT,
                LogPath            TEXT,
                CreatedAt          TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_executions_task   ON TaskExecutions(TaskId, StartedAt DESC);
            CREATE INDEX IF NOT EXISTS idx_executions_status ON TaskExecutions(Status);
            """;
        cmd.ExecuteNonQuery();

        // Migration: add ExcludeDefaultServers column (v0.3.0)
        try
        {
            using var migCmd = conn.CreateCommand();
            migCmd.CommandText = "ALTER TABLE Tasks ADD COLUMN ExcludeDefaultServers INTEGER NOT NULL DEFAULT 0;";
            migCmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }
    }

    /// <summary>Opens a new SQLite connection with foreign keys enabled.</summary>
    SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Enable foreign keys per connection
        using var fk = conn.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON;";
        fk.ExecuteNonQuery();
        return conn;
    }

    #region Task CRUD

    /// <summary>Retrieves a task by its unique identifier.</summary>
    public async Task<RunnerTask?> GetTaskAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tasks WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    /// <summary>Retrieves all tasks, optionally filtered by enabled status and schedule presence.</summary>
    public async Task<List<RunnerTask>> GetAllTasksAsync(string? statusFilter = null, bool? hasSchedule = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        if (statusFilter is "enabled")
            conditions.Add("IsEnabled = 1");
        else if (statusFilter is "disabled")
            conditions.Add("IsEnabled = 0");

        if (hasSchedule == true)
            conditions.Add("Schedule IS NOT NULL");
        else if (hasSchedule == false)
            conditions.Add("Schedule IS NULL");

        var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT * FROM Tasks{where} ORDER BY Name";

        var tasks = new List<RunnerTask>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tasks.Add(ReadTask(reader));

        return tasks;
    }

    /// <summary>Inserts a new task into the database.</summary>
    public async Task InsertTaskAsync(RunnerTask task)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Tasks (Id, Name, Description, Prompt, Schedule, IsEnabled,
                MaxRounds, TimeoutSeconds, AllowedTools, PresetName, McpServers, OutputChannel,
                ExcludeDefaultServers, CreatedAt, UpdatedAt)
            VALUES (@id, @name, @desc, @prompt, @schedule, @enabled,
                @maxRounds, @timeout, @allowedTools, @preset, @mcpServers, @outputChannel,
                @excludeDefaults, @created, @updated)
            """;

        cmd.Parameters.AddWithValue("@id", task.Id);
        cmd.Parameters.AddWithValue("@name", task.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)task.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prompt", task.Prompt);
        cmd.Parameters.AddWithValue("@schedule", (object?)task.Schedule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", task.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@maxRounds", task.Guardrails.MaxRounds);
        cmd.Parameters.AddWithValue("@timeout", task.Guardrails.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@allowedTools",
            task.Guardrails.AllowedTools is not null
                ? JsonSerializer.Serialize(task.Guardrails.AllowedTools, JsonOptions)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@preset", (object?)task.PresetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mcpServers", JsonSerializer.Serialize(task.McpServers, JsonOptions));
        cmd.Parameters.AddWithValue("@outputChannel", (object?)task.OutputChannel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@excludeDefaults", task.ExcludeDefaultServers ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", task.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", task.UpdatedAt.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Updates an existing task in the database.</summary>
    public async Task UpdateTaskAsync(RunnerTask task)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Tasks SET
                Name = @name, Description = @desc, Prompt = @prompt, Schedule = @schedule,
                IsEnabled = @enabled, MaxRounds = @maxRounds, TimeoutSeconds = @timeout,
                AllowedTools = @allowedTools, PresetName = @preset, McpServers = @mcpServers,
                OutputChannel = @outputChannel, ExcludeDefaultServers = @excludeDefaults,
                UpdatedAt = @updated
            WHERE Id = @id
            """;

        cmd.Parameters.AddWithValue("@id", task.Id);
        cmd.Parameters.AddWithValue("@name", task.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)task.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prompt", task.Prompt);
        cmd.Parameters.AddWithValue("@schedule", (object?)task.Schedule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", task.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@maxRounds", task.Guardrails.MaxRounds);
        cmd.Parameters.AddWithValue("@timeout", task.Guardrails.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@allowedTools",
            task.Guardrails.AllowedTools is not null
                ? JsonSerializer.Serialize(task.Guardrails.AllowedTools, JsonOptions)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@preset", (object?)task.PresetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mcpServers", JsonSerializer.Serialize(task.McpServers, JsonOptions));
        cmd.Parameters.AddWithValue("@outputChannel", (object?)task.OutputChannel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@excludeDefaults", task.ExcludeDefaultServers ? 1 : 0);
        cmd.Parameters.AddWithValue("@updated", task.UpdatedAt.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a task and its execution history. Returns (executionsRemoved, logsToDelete).
    /// </summary>
    public async Task<(int ExecutionsRemoved, List<string> LogPaths)> DeleteTaskAsync(string id)
    {
        using var conn = Open();

        // Collect log paths before cascade delete
        var logPaths = new List<string>();
        using (var logCmd = conn.CreateCommand())
        {
            logCmd.CommandText = "SELECT LogPath FROM TaskExecutions WHERE TaskId = @id AND LogPath IS NOT NULL";
            logCmd.Parameters.AddWithValue("@id", id);
            using var reader = await logCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                logPaths.Add(reader.GetString(0));
        }

        // Count executions
        int count;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM TaskExecutions WHERE TaskId = @id";
            countCmd.Parameters.AddWithValue("@id", id);
            count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        }

        // Delete task (cascades to executions)
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM Tasks WHERE Id = @id";
            delCmd.Parameters.AddWithValue("@id", id);
            await delCmd.ExecuteNonQueryAsync();
        }

        return (count, logPaths);
    }

    #endregion

    #region Execution CRUD

    /// <summary>Inserts a new execution record into the database.</summary>
    public async Task InsertExecutionAsync(TaskExecution execution)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TaskExecutions (Id, TaskId, Status, StartedAt, CompletedAt,
                RoundsExecuted, ResultSummary, ErrorMessage, NotificationStatus, LogPath)
            VALUES (@id, @taskId, @status, @startedAt, @completedAt,
                @rounds, @summary, @error, @notifStatus, @logPath)
            """;

        cmd.Parameters.AddWithValue("@id", execution.Id);
        cmd.Parameters.AddWithValue("@taskId", execution.TaskId);
        cmd.Parameters.AddWithValue("@status", execution.Status.ToString());
        cmd.Parameters.AddWithValue("@startedAt", execution.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@completedAt",
            execution.CompletedAt.HasValue ? execution.CompletedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@rounds", execution.RoundsExecuted);
        cmd.Parameters.AddWithValue("@summary", (object?)execution.ResultSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)execution.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notifStatus", (object?)execution.NotificationStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logPath", (object?)execution.LogPath ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Updates an existing execution record with final status and results.</summary>
    public async Task UpdateExecutionAsync(TaskExecution execution)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE TaskExecutions SET
                Status = @status, CompletedAt = @completedAt, RoundsExecuted = @rounds,
                ResultSummary = @summary, ErrorMessage = @error,
                NotificationStatus = @notifStatus, LogPath = @logPath
            WHERE Id = @id
            """;

        cmd.Parameters.AddWithValue("@id", execution.Id);
        cmd.Parameters.AddWithValue("@status", execution.Status.ToString());
        cmd.Parameters.AddWithValue("@completedAt",
            execution.CompletedAt.HasValue ? execution.CompletedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@rounds", execution.RoundsExecuted);
        cmd.Parameters.AddWithValue("@summary", (object?)execution.ResultSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)execution.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notifStatus", (object?)execution.NotificationStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logPath", (object?)execution.LogPath ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Retrieves an execution record by its unique identifier.</summary>
    public async Task<TaskExecution?> GetExecutionAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TaskExecutions WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadExecution(reader) : null;
    }

    /// <summary>Retrieves execution history for a task, ordered by most recent first.</summary>
    public async Task<List<TaskExecution>> GetExecutionsAsync(
        string taskId, int limit = 10, string? statusFilter = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var where = "WHERE TaskId = @taskId";
        if (statusFilter is not null && statusFilter != "all")
            where += " AND Status = @status";

        cmd.CommandText = $"SELECT * FROM TaskExecutions {where} ORDER BY StartedAt DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (statusFilter is not null && statusFilter != "all")
            cmd.Parameters.AddWithValue("@status", statusFilter);

        var executions = new List<TaskExecution>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            executions.Add(ReadExecution(reader));

        return executions;
    }

    /// <summary>Retrieves the most recent execution record for a task.</summary>
    public async Task<TaskExecution?> GetLatestExecutionAsync(string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TaskExecutions WHERE TaskId = @taskId ORDER BY StartedAt DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@taskId", taskId);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadExecution(reader) : null;
    }

    /// <summary>Checks whether the specified task has an execution currently in progress.</summary>
    public async Task<bool> HasRunningExecutionAsync(string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TaskExecutions WHERE TaskId = @taskId AND Status = 'Running'";
        cmd.Parameters.AddWithValue("@taskId", taskId);

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    /// <summary>Returns the total number of executions for a task.</summary>
    public async Task<int> GetExecutionCountAsync(string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TaskExecutions WHERE TaskId = @taskId";
        cmd.Parameters.AddWithValue("@taskId", taskId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    #endregion

    #region Mapping

    /// <summary>Maps a database row to a <see cref="RunnerTask"/> instance.</summary>
    static RunnerTask ReadTask(SqliteDataReader reader)
    {
        var allowedToolsJson = reader.IsDBNull(reader.GetOrdinal("AllowedTools"))
            ? null
            : reader.GetString(reader.GetOrdinal("AllowedTools"));
        var mcpServersJson = reader.GetString(reader.GetOrdinal("McpServers"));

        return new RunnerTask
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description"))
                ? null : reader.GetString(reader.GetOrdinal("Description")),
            Prompt = reader.GetString(reader.GetOrdinal("Prompt")),
            Schedule = reader.IsDBNull(reader.GetOrdinal("Schedule"))
                ? null : reader.GetString(reader.GetOrdinal("Schedule")),
            IsEnabled = reader.GetInt32(reader.GetOrdinal("IsEnabled")) != 0,
            Guardrails = new TaskGuardrails
            {
                MaxRounds = reader.GetInt32(reader.GetOrdinal("MaxRounds")),
                TimeoutSeconds = reader.GetInt32(reader.GetOrdinal("TimeoutSeconds")),
                AllowedTools = allowedToolsJson is not null
                    ? JsonSerializer.Deserialize<List<string>>(allowedToolsJson, JsonOptions)
                    : null,
            },
            PresetName = reader.IsDBNull(reader.GetOrdinal("PresetName"))
                ? null : reader.GetString(reader.GetOrdinal("PresetName")),
            McpServers = JsonSerializer.Deserialize<List<McpServerConfig>>(mcpServersJson, JsonOptions) ?? [],
            ExcludeDefaultServers = !reader.IsDBNull(reader.GetOrdinal("ExcludeDefaultServers"))
                && reader.GetInt32(reader.GetOrdinal("ExcludeDefaultServers")) != 0,
            OutputChannel = reader.IsDBNull(reader.GetOrdinal("OutputChannel"))
                ? null : reader.GetString(reader.GetOrdinal("OutputChannel")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
        };
    }

    /// <summary>Maps a database row to a <see cref="TaskExecution"/> instance.</summary>
    static TaskExecution ReadExecution(SqliteDataReader reader)
    {
        return new TaskExecution
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            TaskId = reader.GetString(reader.GetOrdinal("TaskId")),
            Status = Enum.Parse<ExecutionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("StartedAt"))),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt"))
                ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CompletedAt"))),
            RoundsExecuted = reader.GetInt32(reader.GetOrdinal("RoundsExecuted")),
            ResultSummary = reader.IsDBNull(reader.GetOrdinal("ResultSummary"))
                ? null : reader.GetString(reader.GetOrdinal("ResultSummary")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage"))
                ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            NotificationStatus = reader.IsDBNull(reader.GetOrdinal("NotificationStatus"))
                ? null : reader.GetString(reader.GetOrdinal("NotificationStatus")),
            LogPath = reader.IsDBNull(reader.GetOrdinal("LogPath"))
                ? null : reader.GetString(reader.GetOrdinal("LogPath")),
        };
    }

    #endregion

    /// <summary>Releases the keep-alive connection for in-memory databases.</summary>
    public void Dispose()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }
}
