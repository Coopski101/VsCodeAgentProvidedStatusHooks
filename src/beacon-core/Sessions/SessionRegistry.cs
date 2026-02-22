using System.Collections.Concurrent;
using BeaconCore.Events;

namespace BeaconCore.Sessions;

public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<nint, string> _hwndToSession = new();
    private readonly Lock _lock = new();

    public (SessionInfo session, bool isNew, string? displacedSessionId) RegisterOrUpdate(
        string sessionId,
        nint windowHandle,
        AgentSource source
    )
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.Source = source;
                return (existing, false, null);
            }

            string? displacedId = null;
            if (_hwndToSession.TryGetValue(windowHandle, out var oldSessionId))
            {
                displacedId = oldSessionId;
                RemoveSessionUnsafe(oldSessionId);
            }

            var session = new SessionInfo(sessionId, windowHandle, source);
            _sessions[sessionId] = session;
            _hwndToSession[windowHandle] = sessionId;

            return (session, true, displacedId);
        }
    }

    public SessionInfo? TryGetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public SessionInfo? TryGetSessionByHwnd(nint hwnd)
    {
        if (_hwndToSession.TryGetValue(hwnd, out var sessionId))
            return TryGetSession(sessionId);
        return null;
    }

    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return _sessions.Values.ToList();
    }

    public void EndSession(string sessionId)
    {
        lock (_lock)
        {
            RemoveSessionUnsafe(sessionId);
        }
    }

    public List<SessionInfo> RemoveDeadSessions(Func<nint, bool> isAlive)
    {
        var dead = new List<SessionInfo>();
        lock (_lock)
        {
            foreach (var session in _sessions.Values.ToList())
            {
                if (!isAlive(session.WindowHandle))
                {
                    dead.Add(session);
                    RemoveSessionUnsafe(session.SessionId);
                }
            }
        }
        return dead;
    }

    private void RemoveSessionUnsafe(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.CancelAfkTimer();
            _hwndToSession.TryRemove(session.WindowHandle, out _);
        }
    }
}
