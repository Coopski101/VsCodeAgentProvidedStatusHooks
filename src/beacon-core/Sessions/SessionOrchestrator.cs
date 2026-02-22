using BeaconCore.Config;
using BeaconCore.Events;
using BeaconCore.Hooks;
using BeaconCore.Platform;

namespace BeaconCore.Sessions;

public sealed class SessionOrchestrator : BackgroundService
{
    private readonly SessionRegistry _registry;
    private readonly IPlatformMonitor _monitor;
    private readonly EventBus _bus;
    private readonly BeaconConfig _config;
    private readonly ILogger<SessionOrchestrator> _logger;

    private bool _wasAfk;

    public SessionOrchestrator(
        SessionRegistry registry,
        IPlatformMonitor monitor,
        EventBus bus,
        BeaconConfig config,
        ILogger<SessionOrchestrator> logger
    )
    {
        _registry = registry;
        _monitor = monitor;
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _monitor.StartAsync(stoppingToken);

        _monitor.WindowFocusChanged += OnWindowFocusChanged;

        _logger.LogInformation(
            "Session orchestrator started (AFK threshold={Threshold}s, poll={Poll}ms)",
            _config.AfkThresholdSeconds,
            _config.PollIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);
            PollAfk();
            PollDeadWindows();
        }
    }

    public void HandleStateChange(
        string sessionId,
        AgentSource source,
        HookAction action,
        string hookEvent,
        string reason
    )
    {
        var focusedHwnd = _monitor.FocusedWindowHandle;
        var focusedProcess = _monitor.FocusedWindowProcessName;
        var isVsCodeFocused =
            focusedProcess is not null
            && string.Equals(
                focusedProcess,
                _config.VscodeProcessName,
                StringComparison.OrdinalIgnoreCase
            );

        var windowHandle = nint.Zero;
        if (isVsCodeFocused && focusedHwnd is not null)
        {
            windowHandle = focusedHwnd.Value;
        }
        else
        {
            var existing = _registry.TryGetSession(sessionId);
            if (existing is not null)
                windowHandle = existing.WindowHandle;
        }

        if (windowHandle == nint.Zero)
        {
            _logger.LogWarning(
                "Session {SessionId} has no correlated window handle (focused=0x{FocusedHwnd:X}, process={Process})",
                sessionId,
                focusedHwnd,
                focusedProcess
            );
        }

        var (session, isNew, displacedId) = _registry.RegisterOrUpdate(
            sessionId,
            windowHandle,
            source
        );

        if (displacedId is not null)
        {
            _logger.LogInformation(
                "Session {OldSession} displaced by {NewSession} on hwnd 0x{Hwnd:X}",
                displacedId,
                sessionId,
                windowHandle
            );
            PublishWire(
                new BeaconEvent
                {
                    EventType = BeaconEventType.SessionEnded,
                    SessionId = displacedId,
                    Source = source,
                    HookEvent = "SessionDisplaced",
                    Reason = $"Replaced by new session {sessionId}",
                }
            );
        }

        if (isNew)
        {
            _logger.LogInformation(
                "New session {SessionId} registered on hwnd 0x{Hwnd:X} ({Source})",
                sessionId,
                windowHandle,
                source
            );
            PublishWire(
                new BeaconEvent
                {
                    EventType = BeaconEventType.SessionStarted,
                    SessionId = sessionId,
                    Source = source,
                    HookEvent = hookEvent,
                    Reason = $"Session started ({source})",
                }
            );
        }

        switch (action)
        {
            case HookAction.Clear:
                HandleClear(session, hookEvent, reason);
                break;
            case HookAction.Waiting:
            case HookAction.Done:
                HandleWaitingOrDone(session, action, hookEvent, reason);
                break;
        }
    }

    private void HandleClear(SessionInfo session, string hookEvent, string reason)
    {
        session.CancelAfkTimer();
        session.InternalState = BeaconMode.Idle;

        if (session.PublishedState is BeaconMode.Waiting or BeaconMode.Done)
        {
            session.PublishedState = BeaconMode.Idle;
            PublishWire(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Clear,
                    SessionId = session.SessionId,
                    Source = session.Source,
                    HookEvent = hookEvent,
                    Reason = reason,
                }
            );
        }
        else
        {
            session.PublishedState = BeaconMode.Idle;
        }
    }

    private void HandleWaitingOrDone(
        SessionInfo session,
        HookAction action,
        string hookEvent,
        string reason
    )
    {
        var mode = action == HookAction.Waiting ? BeaconMode.Waiting : BeaconMode.Done;
        var wireType = ToWireEvent(action);
        session.CancelAfkTimer();
        session.InternalState = mode;
        session.StateChangedAt = DateTimeOffset.UtcNow;
        session.InputTickAtStateChange = _monitor.LastInputTick;

        var isFocused =
            _monitor.FocusedWindowHandle == session.WindowHandle
            && session.WindowHandle != nint.Zero;

        if (!isFocused)
        {
            session.PublishedState = mode;
            _logger.LogInformation(
                "Session {Session} window not focused — publishing {Event} immediately",
                session.SessionId,
                wireType
            );
            PublishWire(
                new BeaconEvent
                {
                    EventType = wireType,
                    SessionId = session.SessionId,
                    Source = session.Source,
                    HookEvent = hookEvent,
                    Reason = reason,
                }
            );
        }
        else
        {
            _logger.LogInformation(
                "Session {Session} window is focused — starting AFK timer ({Threshold}s)",
                session.SessionId,
                _config.AfkThresholdSeconds
            );
            StartAfkTimer(session, action, hookEvent, reason);
        }
    }

    private void StartAfkTimer(
        SessionInfo session,
        HookAction action,
        string hookEvent,
        string reason
    )
    {
        var wireType = ToWireEvent(action);
        var cts = new CancellationTokenSource();
        session.AfkTimerCts = cts;
        var capturedTickAtChange = session.InputTickAtStateChange;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.AfkThresholdSeconds), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var currentTick = _monitor.LastInputTick;
            if (currentTick != capturedTickAtChange)
            {
                _logger.LogDebug(
                    "Session {Session} AFK timer expired but user was active — skipping publish",
                    session.SessionId
                );
                return;
            }

            var expectedMode = action == HookAction.Waiting ? BeaconMode.Waiting : BeaconMode.Done;
            if (session.InternalState != expectedMode)
                return;

            session.PublishedState = expectedMode;
            _logger.LogInformation(
                "Session {Session} AFK timer expired with no input — publishing {Event}",
                session.SessionId,
                wireType
            );
            PublishWire(
                new BeaconEvent
                {
                    EventType = wireType,
                    SessionId = session.SessionId,
                    Source = session.Source,
                    HookEvent = hookEvent,
                    Reason = reason,
                }
            );
        });
    }

    private void OnWindowFocusChanged(nint hwnd, string processName)
    {
        var isVsCode = string.Equals(
            processName,
            _config.VscodeProcessName,
            StringComparison.OrdinalIgnoreCase
        );

        if (!isVsCode)
            return;

        var session = _registry.TryGetSessionByHwnd(hwnd);
        if (session is null)
            return;

        if (session.PublishedState is BeaconMode.Waiting or BeaconMode.Done)
        {
            _logger.LogInformation(
                "Session {Session} window gained focus while {State} — publishing Clear",
                session.SessionId,
                session.PublishedState
            );

            session.CancelAfkTimer();
            session.InternalState = BeaconMode.Idle;
            session.PublishedState = BeaconMode.Idle;
            PublishWire(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Clear,
                    SessionId = session.SessionId,
                    Source = session.Source,
                    HookEvent = "FocusGained",
                    Reason = "VS Code window gained focus",
                }
            );
        }
    }

    private void PollAfk()
    {
        var idle = _monitor.UserIdleDuration;
        var isAfk = idle > TimeSpan.FromSeconds(_config.AfkThresholdSeconds);

        if (_wasAfk && !isAfk)
        {
            _logger.LogDebug("User returned from AFK (idle {Idle})", idle);

            var focusedHwnd = _monitor.FocusedWindowHandle;
            if (focusedHwnd is null)
            {
                _wasAfk = isAfk;
                return;
            }

            var session = _registry.TryGetSessionByHwnd(focusedHwnd.Value);
            if (
                session is not null
                && session.PublishedState is BeaconMode.Waiting or BeaconMode.Done
            )
            {
                _logger.LogInformation(
                    "AFK return with session {Session} focused — publishing Clear",
                    session.SessionId
                );

                session.CancelAfkTimer();
                session.InternalState = BeaconMode.Idle;
                session.PublishedState = BeaconMode.Idle;
                PublishWire(
                    new BeaconEvent
                    {
                        EventType = BeaconEventType.Clear,
                        SessionId = session.SessionId,
                        Source = session.Source,
                        HookEvent = "AfkReturn",
                        Reason = "User returned from AFK while session window is focused",
                    }
                );
            }
        }

        _wasAfk = isAfk;
    }

    private void PollDeadWindows()
    {
        var dead = _registry.RemoveDeadSessions(hwnd => _monitor.IsWindowAlive(hwnd));
        foreach (var session in dead)
        {
            _logger.LogInformation(
                "Session {Session} window (hwnd=0x{Hwnd:X}) closed — ending session",
                session.SessionId,
                session.WindowHandle
            );
            PublishWire(
                new BeaconEvent
                {
                    EventType = BeaconEventType.SessionEnded,
                    SessionId = session.SessionId,
                    Source = session.Source,
                    HookEvent = "WindowClosed",
                    Reason = "VS Code window closed",
                }
            );
        }
    }

    private static BeaconEventType ToWireEvent(HookAction action) =>
        action switch
        {
            HookAction.Waiting => BeaconEventType.Waiting,
            HookAction.Done => BeaconEventType.Done,
            HookAction.Clear => BeaconEventType.Clear,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private void PublishWire(BeaconEvent evt)
    {
        _bus.Publish(evt);
    }

    public override void Dispose()
    {
        _monitor.WindowFocusChanged -= OnWindowFocusChanged;
        _monitor.Dispose();
        base.Dispose();
    }
}
