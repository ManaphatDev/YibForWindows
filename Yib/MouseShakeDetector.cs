using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yib;

internal enum ShakeSensitivity
{
    Loose,
    Medium,
    Tight
}

internal sealed class MouseShakeDetector : IDisposable
{
    private readonly record struct Threshold(int Reversals, int Distance);

    private static readonly Dictionary<ShakeSensitivity, Threshold> Thresholds = new()
    {
        [ShakeSensitivity.Loose] = new Threshold(3, 150),
        [ShakeSensitivity.Medium] = new Threshold(4, 220),
        [ShakeSensitivity.Tight] = new Threshold(6, 320),
    };

    private const int WindowMilliseconds = 600;
    private const int CooldownMilliseconds = 400;
    private const int NoiseDeadZone = 2;

    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly List<(int X, long Time)> _history = new();
    private IntPtr _hookId;
    private long _lastTriggerTime = -CooldownMilliseconds;

    public ShakeSensitivity Sensitivity { get; set; } = ShakeSensitivity.Medium;

    // Set to false while the dial overlay is open, so aiming the mouse around the
    // ring can't be misread as another shake and pop a second dial.
    public bool Enabled { get; set; } = true;

    public event Action<Point>? ShakeDetected;

    public MouseShakeDetector()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule curModule = curProcess.MainModule!;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to install mouse hook (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // This runs for every mouse move on the OS hook chain. An exception escaping here
        // would be unhandled (reverse P/Invoke) and crash the app, so it's swallowed - but
        // CallNextHookEx must ALWAYS run, or system-wide mouse input would stall.
        try
        {
            if (nCode >= 0 && (long)wParam == NativeMethods.WM_MOUSEMOVE)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                ProcessMove(hookStruct.pt.X, hookStruct.pt.Y);
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ProcessMove(int x, int y)
    {
        if (!Enabled)
        {
            _history.Clear();
            return;
        }

        long now = Environment.TickCount64;

        if (now - _lastTriggerTime < CooldownMilliseconds)
        {
            _history.Clear();
            return;
        }

        _history.Add((x, now));

        long cutoff = now - WindowMilliseconds;
        int removeCount = 0;
        while (removeCount < _history.Count && _history[removeCount].Time < cutoff)
        {
            removeCount++;
        }
        if (removeCount > 0)
        {
            _history.RemoveRange(0, removeCount);
        }

        if (_history.Count < 3)
        {
            return;
        }

        int reversals = 0;
        int distance = 0;
        int direction = 0;

        for (int i = 1; i < _history.Count; i++)
        {
            int dx = _history[i].X - _history[i - 1].X;
            if (Math.Abs(dx) < NoiseDeadZone)
            {
                continue;
            }

            distance += Math.Abs(dx);
            int newDirection = Math.Sign(dx);

            if (direction != 0 && newDirection != direction)
            {
                reversals++;
            }

            direction = newDirection;
        }

        Threshold threshold = Thresholds[Sensitivity];
        if (reversals >= threshold.Reversals && distance >= threshold.Distance)
        {
            _lastTriggerTime = now;
            _history.Clear();
            ShakeDetected?.Invoke(new Point(x, y));
        }
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
