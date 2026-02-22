using BeaconCore.Config;
using BeaconCore.Events;

namespace BeaconCore.Hooks;

public sealed record NormalizedHookResult(
    HookAction Action,
    AgentSource Source,
    string HookEvent,
    string Reason,
    string? TranscriptPath = null
);

public sealed class HookNormalizer
{
    private readonly BeaconConfig _config;
    private readonly ILogger<HookNormalizer> _logger;

    public HookNormalizer(BeaconConfig config, ILogger<HookNormalizer> logger)
    {
        _config = config;
        _logger = logger;
    }

    public NormalizedHookResult? Normalize(HookPayload payload)
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
            _logger.LogTrace(
                "No EventMapping found for key '{MappingKey}' in {Agent} mappings, ignoring",
                mappingKey,
                agent
            );
            return null;
        }

        if (string.Equals(targetStr, "WatchTranscript", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.TranscriptPath is null)
            {
                _logger.LogDebug(
                    "'{MappingKey}' mapped to WatchTranscript but no transcript_path in payload",
                    mappingKey
                );
                return null;
            }

            _logger.LogInformation(
                "'{MappingKey}' -> WatchTranscript (source: {Agent})",
                mappingKey,
                agent
            );
            return new NormalizedHookResult(
                HookAction.WatchTranscript,
                agent,
                hookEvent,
                BuildReason(hookEvent, payload),
                payload.TranscriptPath
            );
        }

        if (!Enum.TryParse<HookAction>(targetStr, ignoreCase: true, out var action))
        {
            _logger.LogWarning(
                "EventMapping '{MappingKey}' has invalid target '{Target}' — expected Waiting, Done, Clear, or WatchTranscript",
                mappingKey,
                targetStr
            );
            return null;
        }

        _logger.LogInformation(
            "Mapped '{MappingKey}' -> {Action} (source: {Agent})",
            mappingKey,
            action,
            agent
        );

        return new NormalizedHookResult(action, agent, hookEvent, BuildReason(hookEvent, payload));
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
