using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Detailed execution log written to logs/{execution-id}.json.
/// Contains full conversation history for audit and debugging.
/// </summary>
public sealed class ExecutionLog
{
    /// <summary>JSON serialization options for log output.</summary>
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Unique execution identifier.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Reference to the parent task.</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable name of the executed task.</summary>
    public required string TaskName { get; init; }

    /// <summary>When execution started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When execution completed (null if still running).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Current execution status string.</summary>
    public string Status { get; set; } = "Running";

    /// <summary>Notification delivery status: sent, failed, or skipped.</summary>
    public string? NotificationStatus { get; set; }

    /// <summary>Ordered list of LLM interaction rounds.</summary>
    public List<RoundLog> Rounds { get; set; } = [];

    /// <summary>
    /// Saves this log to the specified file path.
    /// </summary>
    public void Save(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(filePath, json);
    }
}

/// <summary>
/// A single round of LLM interaction in the execution log.
/// </summary>
public sealed class RoundLog
{
    /// <summary>1-based round number within the execution.</summary>
    public int Round { get; set; }

    /// <summary>The user/system request message sent to the LLM.</summary>
    public RoundMessage? Request { get; set; }

    /// <summary>The LLM response message.</summary>
    public RoundMessage? Response { get; set; }

    /// <summary>Tool results returned during this round.</summary>
    public List<ToolResultLog>? ToolResults { get; set; }
}

/// <summary>
/// A message entry in the round log.
/// </summary>
public sealed class RoundMessage
{
    /// <summary>Message role (e.g., "user", "assistant").</summary>
    public string Role { get; set; } = "";

    /// <summary>Text content of the message.</summary>
    public string? Content { get; set; }

    /// <summary>Tool calls requested by the LLM in this message.</summary>
    public List<ToolCallLog>? ToolCalls { get; set; }
}

/// <summary>
/// A tool call entry in the round log.
/// </summary>
public sealed class ToolCallLog
{
    /// <summary>Provider-assigned tool call identifier.</summary>
    public string Id { get; set; } = "";

    /// <summary>Name of the tool being invoked.</summary>
    public string Name { get; set; } = "";

    /// <summary>Raw JSON arguments passed to the tool.</summary>
    [JsonConverter(typeof(RawJsonConverter))]
    public string? Arguments { get; set; }
}

/// <summary>
/// A tool result entry in the round log.
/// </summary>
public sealed class ToolResultLog
{
    /// <summary>Identifier of the tool call this result corresponds to.</summary>
    public string ToolCallId { get; set; } = "";

    /// <summary>Text content returned by the tool.</summary>
    public string Content { get; set; } = "";
}

/// <summary>
/// Serializes a raw JSON string without double-encoding.
/// </summary>
sealed class RawJsonConverter : JsonConverter<string>
{
    /// <summary>Reads a JSON token and returns its raw text representation.</summary>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    /// <summary>Writes a raw JSON string directly without double-encoding.</summary>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            doc.RootElement.WriteTo(writer);
        }
        catch
        {
            writer.WriteStringValue(value);
        }
    }
}
