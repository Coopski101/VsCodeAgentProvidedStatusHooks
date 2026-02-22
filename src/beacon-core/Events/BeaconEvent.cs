using System.Text.Json.Serialization;

namespace BeaconCore.Events;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeaconEventType
{
    Waiting,
    Done,
    Clear,
    SessionStarted,
    SessionEnded,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentSource
{
    Copilot,
    ClaudeCode,
    Unknown,
}

public sealed class BeaconEvent
{
    [JsonPropertyName("eventType")]
    public required BeaconEventType EventType { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("source")]
    public required AgentSource Source { get; init; }

    [JsonPropertyName("hookEvent")]
    public required string HookEvent { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
