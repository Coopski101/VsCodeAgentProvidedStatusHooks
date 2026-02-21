using BeaconCore.Config;
using BeaconCore.Events;
using Microsoft.Extensions.Logging;

namespace BeaconCore.Hooks;

public sealed class HookNormalizer
{
    private readonly BeaconConfig _config;
    private readonly ILogger<HookNormalizer> _logger;

    public HookNormalizer(BeaconConfig config, ILogger<HookNormalizer> logger)
    {
        _config = config;
        _logger = logger;
    }

    public BeaconEvent? Normalize(HookPayload payload)
    {
        if (payload.StopHookActive == true)
        {
            _logger.LogDebug("Skipping payload: stop_hook_active is true");
            return null;
        }

        var hookEvent = payload.ResolvedHookEventName;
        var agent = DetectAgent(payload);

        _logger.LogDebug("Received hook event '{HookEvent}' from {Agent}", hookEvent, agent);

        var mappingKey = ResolveMappingKey(hookEvent, payload);
        if (mappingKey is null)
        {
            _logger.LogDebug("Could not resolve mapping key for '{HookEvent}'", hookEvent);
            return null;
        }

        var mappings = agent switch
        {
            AgentSource.ClaudeCode => _config.ClaudeEventMappings,
            AgentSource.Copilot => _config.CopilotEventMappings,
            _ => _config.CopilotEventMappings,
        };

        if (!mappings.TryGetValue(mappingKey, out var targetStr))
        {
            _logger.LogDebug(
                "No EventMapping found for key '{MappingKey}' in {Agent} mappings, ignoring",
                mappingKey,
                agent
            );
            return null;
        }

        if (!Enum.TryParse<BeaconEventType>(targetStr, ignoreCase: true, out var eventType))
        {
            _logger.LogWarning(
                "EventMapping '{MappingKey}' has invalid target '{Target}' — expected Waiting, Done, or Clear",
                mappingKey,
                targetStr
            );
            return null;
        }

        _logger.LogInformation(
            "Mapped '{MappingKey}' -> {EventType} (source: {Agent})",
            mappingKey,
            eventType,
            agent
        );

        return new BeaconEvent
        {
            EventType = eventType,
            Source = agent,
            HookEvent = hookEvent,
            Reason = BuildReason(hookEvent, payload),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static string? ResolveMappingKey(string hookEvent, HookPayload payload)
    {
        if (
            string.Equals(hookEvent, "Notification", StringComparison.OrdinalIgnoreCase)
            && payload.NotificationType is not null
        )
        {
            return $"Notification:{payload.NotificationType}";
        }

        return hookEvent;
    }

    private static AgentSource DetectAgent(HookPayload payload)
    {
        if (payload.ClaudeHookEventName is not null)
            return AgentSource.ClaudeCode;

        if (payload.CopilotHookEventName is not null)
            return AgentSource.Copilot;

        return AgentSource.Unknown;
    }

    private static string BuildReason(string hookEvent, HookPayload payload)
    {
        return hookEvent switch
        {
            "Stop" => "Agent finished responding",
            "SubagentStop" => $"Subagent completed ({payload.AgentType ?? "unknown"})",
            "Notification" =>
                $"Notification: {payload.Message ?? payload.NotificationType ?? "unknown"}",
            "PermissionRequest" => $"Permission requested for {payload.ToolName ?? "unknown tool"}",
            "UserPromptSubmit" => "User submitted a prompt",
            "SessionStart" => $"Session started ({payload.Source ?? "new"})",
            _ => $"Hook event: {hookEvent}",
        };
    }
}
