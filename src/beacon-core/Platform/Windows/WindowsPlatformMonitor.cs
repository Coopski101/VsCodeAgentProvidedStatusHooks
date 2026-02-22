using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BeaconCore.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformMonitor : IPlatformMonitor
{
    private readonly ILogger<WindowsPlatformMonitor> _logger;
    private uint _messagePumpThreadId;
    private nint? _lastFocusedHwnd;
    private string? _lastFocusedProcessName;

    public nint? FocusedWindowHandle => _lastFocusedHwnd;
    public string? FocusedWindowProcessName => _lastFocusedProcessName;
    public event Action<nint, string>? WindowFocusChanged;

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
                cbSize = (uint)Marshal.SizeOf<Win32.LASTINPUTINFO>(),
            };
            if (!Win32.GetLastInputInfo(ref info))
                return TimeSpan.Zero;

            var idleMs = (uint)Environment.TickCount - info.dwTime;
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }

    public uint LastInputTick
    {
        get
        {
            var info = new Win32.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<Win32.LASTINPUTINFO>(),
            };
            return Win32.GetLastInputInfo(ref info) ? info.dwTime : 0;
        }
    }

    public bool IsWindowAlive(nint hwnd)
    {
        return Win32.IsWindow(hwnd);
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

        ReadCurrentForeground();

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

        tcs.SetResult();

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
    }

    private void ReadCurrentForeground()
    {
        try
        {
            var hwnd = Win32.GetForegroundWindow();
            if (hwnd == nint.Zero)
                return;

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return;

            var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            _logger.LogInformation("Initial foreground: {Process} (hwnd=0x{Hwnd:X})", name, hwnd);
            _lastFocusedHwnd = hwnd;
            _lastFocusedProcessName = name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read initial foreground window");
        }
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
            var name = process.ProcessName;
            _logger.LogDebug("Foreground changed to {Process} (hwnd=0x{Hwnd:X})", name, hwnd);
            _lastFocusedHwnd = hwnd;
            _lastFocusedProcessName = name;

            WindowFocusChanged?.Invoke(hwnd, name);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error in foreground callback");
        }
    }
}
