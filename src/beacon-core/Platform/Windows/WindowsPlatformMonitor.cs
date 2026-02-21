using System.Diagnostics;
using System.Runtime.Versioning;

namespace BeaconCore.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformMonitor : IPlatformMonitor
{
    private readonly ILogger<WindowsPlatformMonitor> _logger;
    private uint _messagePumpThreadId;
    private string? _lastFocusedProcessName;

    public event Action<string>? AppFocusGained;

    public WindowsPlatformMonitor(ILogger<WindowsPlatformMonitor> logger)
    {
        _logger = logger;
    }

    public TimeSpan UserIdleDuration
    {
        get
        {
            var info = new Win32.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.LASTINPUTINFO>(),
            };
            if (!Win32.GetLastInputInfo(ref info))
                return TimeSpan.Zero;

            var idleMs = (uint)Environment.TickCount - info.dwTime;
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }

    public bool IsAppFocused(string processName)
    {
        return string.Equals(
            _lastFocusedProcessName,
            processName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    public Task StartAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() => RunMessageLoop(tcs, ct))
        {
            IsBackground = true,
            Name = "PlatformMonitor-MessagePump",
        };
        thread.Start();
        return tcs.Task;
    }

    public void Dispose() { }

    private void RunMessageLoop(TaskCompletionSource tcs, CancellationToken ct)
    {
        _messagePumpThreadId = Win32.GetCurrentThreadId();

        Win32.WinEventDelegate callback = OnForegroundChanged;

        var hook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND,
            Win32.EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            callback,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT
        );

        if (hook == nint.Zero)
        {
            _logger.LogError("SetWinEventHook failed");
            tcs.SetResult();
            return;
        }

        _logger.LogInformation("Windows platform monitor hook installed");

        ct.Register(() =>
            Win32.PostThreadMessage(_messagePumpThreadId, Win32.WM_QUIT, nint.Zero, nint.Zero)
        );

        while (Win32.GetMessage(out var msg, nint.Zero, 0, 0))
        {
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        Win32.UnhookWinEvent(hook);
        _logger.LogInformation("Windows platform monitor hook removed");

        GC.KeepAlive(callback);
        tcs.SetResult();
    }

    private void OnForegroundChanged(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    )
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return;

            var process = Process.GetProcessById((int)pid);
            _lastFocusedProcessName = process.ProcessName;

            AppFocusGained?.Invoke(process.ProcessName);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error in foreground callback");
        }
    }
}
