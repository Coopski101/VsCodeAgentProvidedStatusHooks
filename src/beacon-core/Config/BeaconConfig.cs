namespace BeaconCore.Config;

public sealed class BeaconConfig
{
    public int Port { get; set; } = 17321;
    public string VscodeProcessName { get; set; } = "Code";
    public int AfkThresholdSeconds { get; set; } = 30;
    public int PollIntervalMs { get; set; } = 250;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int AutoApprovedToolDetectionDelayMs { get; set; } = 4000;
    public bool FakeMode { get; set; } = false;

    public Dictionary<string, string> CopilotEventMappings { get; set; } = DefaultCopilotMappings();

    public Dictionary<string, string> ClaudeEventMappings { get; set; } = DefaultClaudeMappings();

    private static Dictionary<string, string> DefaultCopilotMappings() =>
        new()
        {
            ["Stop"] = "Done",
            ["PreToolUse"] = "WatchTranscript",
            ["UserPromptSubmit"] = "Clear",
            ["SessionStart"] = "Clear",
        };

    private static Dictionary<string, string> DefaultClaudeMappings() =>
        new()
        {
            ["Stop"] = "Done",
            ["SubagentStop"] = "Done",
            ["PermissionRequest"] = "Waiting",
            ["Notification:permission_prompt"] = "Waiting",
            ["Notification:idle_prompt"] = "Waiting",
            ["UserPromptSubmit"] = "Clear",
            ["SessionStart"] = "Clear",
        };
}
