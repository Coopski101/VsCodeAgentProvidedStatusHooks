using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using AgenticUnattended.Sessions;
using AgenticUnattended.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgenticUnattended.Tests;

public sealed class SessionStateMachineTests : IDisposable
{
    private readonly FakePlatformMonitor _monitor = new();
    private readonly EventBus _bus = new();
    private readonly SessionRegistry _registry = new();
    private readonly BeaconConfig _config = new() { AfkThresholdSeconds = 5, DoneDebounceMs = 0 };
    private readonly EventCollector _events;
    private readonly SessionStateMachine _sm;

    public SessionStateMachineTests()
    {
        _events = new EventCollector(_bus);
        _sm = new SessionStateMachine(
            _registry,
            _monitor,
            _bus,
            _config,
            TimeProvider.System,
            NullLogger<SessionStateMachine>.Instance
        );
    }

    public void Dispose() => _events.Dispose();

    [Fact]
    public void HandleStateChange_Done_NotFocused_PublishesImmediately()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        Assert.Equal(2, _events.Events.Count);
        Assert.Equal(BeaconEventType.SessionStarted, _events.Events[0].EventType);
        Assert.Equal(BeaconEventType.Done, _events.Events[1].EventType);
    }

    [Fact]
    public void HandleStateChange_Waiting_NotFocused_PublishesImmediately()
    {
        _sm.HandleStateChange("s1", AgentSource.ClaudeCode, HookAction.Waiting, "PermReq", "waiting");

        var waitingEvent = _events.Events.First(e => e.EventType == BeaconEventType.Waiting);
        Assert.Equal("s1", waitingEvent.SessionId);
        Assert.Equal(AgentSource.ClaudeCode, waitingEvent.Source);
    }

    [Fact]
    public void HandleStateChange_Done_Focused_StartsAfkTimer_DoesNotPublishImmediately()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _monitor.MarkWindowAlive(100);

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(BeaconMode.Done, session.InternalState);
        Assert.Equal(BeaconMode.Idle, session.PublishedState);
        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);
    }

    [Fact]
    public void HandleStateChange_Clear_ResetsDoneState()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "UserPrompt", "clear");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(BeaconMode.Idle, session.InternalState);
        Assert.Equal(BeaconMode.Idle, session.PublishedState);
        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Clear);
    }

    [Fact]
    public void HandleStateChange_Clear_WhenAlreadyIdle_DoesNotPublishClear()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "UserPrompt", "clear");

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Clear);
    }

    [Fact]
    public void HandleStateChange_NewSession_PublishesSessionStarted()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.SessionStarted);
    }

    [Fact]
    public void HandleStateChange_ExistingSession_DoesNotPublishSessionStartedAgain()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Clear", "clear");

        Assert.Single(_events.Events, e => e.EventType == BeaconEventType.SessionStarted);
    }

    [Fact]
    public void HandleStateChange_DisplacesOldSession_PublishesSessionEnded()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _sm.HandleStateChange("old", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        _sm.HandleStateChange("new", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.SessionEnded && e.SessionId == "old");
    }

    [Fact]
    public void HandleStateChange_VsCodeFocused_AssignsHwnd()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal((nint)100, session.WindowHandle);
    }

    [Fact]
    public void HandleStateChange_CliFocused_ClaudeCode_AssignsHwnd()
    {
        _monitor.FocusedWindowHandle = 200;
        _monitor.FocusedWindowProcessName = "powershell";

        _sm.HandleStateChange("s1", AgentSource.ClaudeCode, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal((nint)200, session.WindowHandle);
    }

    [Fact]
    public void HandleStateChange_CliFocused_Copilot_DoesNotAssignHwnd()
    {
        _monitor.FocusedWindowHandle = 200;
        _monitor.FocusedWindowProcessName = "powershell";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(nint.Zero, session.WindowHandle);
    }

    [Fact]
    public void HandleStateChange_LateBindsHwnd_OnExistingSession()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(nint.Zero, session.WindowHandle);

        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Clear", "clear");

        Assert.Equal((nint)100, session.WindowHandle);
    }

    [Fact]
    public void OnWindowFocusChanged_VsCode_ClearsPublishedDone()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(BeaconMode.Done, session.PublishedState);

        _sm.OnWindowFocusChanged(session.WindowHandle, "Code");

        Assert.Equal(BeaconMode.Idle, session.PublishedState);
        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear && e.HookEvent == "FocusGained");
    }

    [Fact]
    public void OnWindowFocusChanged_NonVsCode_DoesNothing()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        var session = _registry.TryGetSession("s1")!;
        var countBefore = _events.Events.Count;

        _sm.OnWindowFocusChanged(session.WindowHandle, "chrome");

        Assert.Equal(countBefore, _events.Events.Count);
    }

    [Fact]
    public void OnWindowFocusChanged_AdoptsOrphanedSession()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(nint.Zero, session.WindowHandle);

        _sm.OnWindowFocusChanged(100, "Code");

        Assert.Equal((nint)100, session.WindowHandle);
        Assert.Equal(BeaconMode.Idle, session.PublishedState);
        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear && e.HookEvent == "FocusGained");
    }

    [Fact]
    public void OnWindowFocusChanged_IdleSession_DoesNotPublishClear()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Clear", "cleared");

        var countBefore = _events.Events.Count;
        _sm.OnWindowFocusChanged(100, "Code");

        Assert.Equal(countBefore, _events.Events.Count);
    }

    [Fact]
    public void PollDeadWindows_RemovesDeadSessions()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _monitor.MarkWindowAlive(100);
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        _monitor.MarkWindowDead(100);
        _sm.PollDeadWindows();

        Assert.Null(_registry.TryGetSession("s1"));
        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.SessionEnded && e.HookEvent == "WindowClosed");
    }

    [Fact]
    public void PollAfk_ReturnFromAfk_ClearsPublishedState()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _monitor.MarkWindowAlive(100);
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        session.PublishedState = BeaconMode.Done;

        _monitor.UserIdleDuration = TimeSpan.FromSeconds(60);
        _sm.PollAfk();

        _monitor.UserIdleDuration = TimeSpan.FromSeconds(0);
        _sm.PollAfk();

        Assert.Equal(BeaconMode.Idle, session.PublishedState);
        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear && e.HookEvent == "AfkReturn");
    }

    [Fact]
    public void PollAfk_ReturnFromAfk_IdleSession_DoesNotPublishClear()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _monitor.MarkWindowAlive(100);
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Clear", "cleared");

        _monitor.UserIdleDuration = TimeSpan.FromSeconds(60);
        _sm.PollAfk();

        var countBefore = _events.Events.Count;
        _monitor.UserIdleDuration = TimeSpan.FromSeconds(0);
        _sm.PollAfk();

        Assert.DoesNotContain(_events.Events.Skip(countBefore), e => e.EventType == BeaconEventType.Clear);
    }

    [Fact]
    public void HandleStateChange_Waiting_ThenDone_OverridesState()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Waiting, "Perm", "waiting");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(BeaconMode.Done, session.InternalState);
        Assert.Equal(BeaconMode.Done, session.PublishedState);
    }

    [Fact]
    public void HandleStateChange_Done_ThenClear_ThenDone_CyclesCorrectly()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Prompt", "clear");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done again");

        var session = _registry.TryGetSession("s1")!;
        Assert.Equal(BeaconMode.Done, session.PublishedState);
        Assert.Equal(3, _events.Events.Count(e =>
            e.EventType is BeaconEventType.Done or BeaconEventType.Clear));
    }
}

public sealed class SessionStateMachineDoneDebounceTests : IDisposable
{
    private readonly FakePlatformMonitor _monitor = new();
    private readonly EventBus _bus = new();
    private readonly SessionRegistry _registry = new();
    private readonly BeaconConfig _config = new() { AfkThresholdSeconds = 5, DoneDebounceMs = 3000 };
    private readonly FakeTimeProvider _time = new();
    private readonly EventCollector _events;
    private readonly SessionStateMachine _sm;

    public SessionStateMachineDoneDebounceTests()
    {
        _events = new EventCollector(_bus);
        _sm = new SessionStateMachine(
            _registry,
            _monitor,
            _bus,
            _config,
            _time,
            NullLogger<SessionStateMachine>.Instance
        );
    }

    public void Dispose() => _events.Dispose();

    [Fact]
    public async Task Done_NotFocused_PublishesAfterDebounce()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        await Task.Delay(50);

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);

        _time.Advance(TimeSpan.FromMilliseconds(3001));
        await Task.Delay(50);

        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Done);
    }

    [Fact]
    public async Task Done_CancelledByClear_DoesNotPublish()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Prompt", "clear");

        _time.Advance(TimeSpan.FromMilliseconds(5000));
        await Task.Delay(50);

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);
    }

    [Fact]
    public async Task Done_CancelledByWaiting_DoesNotPublish()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Waiting, "Perm", "waiting");

        _time.Advance(TimeSpan.FromMilliseconds(5000));
        await Task.Delay(50);

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);
        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Waiting);
    }

    [Fact]
    public async Task Done_Focused_PublishesAfterDebounce_ThenAfkTimer()
    {
        _monitor.FocusedWindowHandle = 100;
        _monitor.FocusedWindowProcessName = "Code";
        _monitor.MarkWindowAlive(100);

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done");
        await Task.Delay(50);

        _time.Advance(TimeSpan.FromMilliseconds(3001));
        await Task.Delay(50);

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);

        var session = _registry.TryGetSession("s1")!;
        Assert.NotNull(session.AfkTimerCts);
    }

    [Fact]
    public async Task Done_RepeatedStops_ResetsDebounce()
    {
        _monitor.FocusedWindowHandle = 999;
        _monitor.FocusedWindowProcessName = "explorer";

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done1");
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromMilliseconds(2000));
        await Task.Delay(50);

        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Done, "Stop", "done2");
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromMilliseconds(2000));
        await Task.Delay(50);

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Done);

        _time.Advance(TimeSpan.FromMilliseconds(1500));
        await Task.Delay(50);

        Assert.Single(_events.Events, e => e.EventType == BeaconEventType.Done);
    }
}
