using System.Text.Json.Serialization;

namespace BeaconCore.Hooks;

public sealed class HookPayload
{
    [JsonPropertyName("hook_event_name")]
    public string? ClaudeHookEventName { get; set; }

    [JsonPropertyName("hookEventName")]
    public string? CopilotHookEventName { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("sessionId")]
    public string? CopilotSessionId { get; set; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("stop_hook_active")]
    public bool? StopHookActive { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    public string ResolvedHookEventName => ClaudeHookEventName ?? CopilotHookEventName ?? "Unknown";

    public string ResolvedSessionId => SessionId ?? CopilotSessionId ?? "unknown";
}
