using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.AssistStudio.Runner.Models;

/// <summary>
/// Detailed execution log written to logs/{execution-id}.json.
/// Contains full conversation history for audit and debugging.
/// </summary>
public sealed class ExecutionLog
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string ExecutionId { get; init; }
    public required string TaskId { get; init; }
    public required string TaskName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "Running";
    public string? NotificationStatus { get; set; }
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
    public int Round { get; set; }
    public RoundMessage? Request { get; set; }
    public RoundMessage? Response { get; set; }
    public List<ToolResultLog>? ToolResults { get; set; }
}

/// <summary>
/// A message entry in the round log.
/// </summary>
public sealed class RoundMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<ToolCallLog>? ToolCalls { get; set; }
}

/// <summary>
/// A tool call entry in the round log.
/// </summary>
public sealed class ToolCallLog
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    [JsonConverter(typeof(RawJsonConverter))]
    public string? Arguments { get; set; }
}

/// <summary>
/// A tool result entry in the round log.
/// </summary>
public sealed class ToolResultLog
{
    public string ToolCallId { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// Serializes a raw JSON string without double-encoding.
/// </summary>
sealed class RawJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

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
