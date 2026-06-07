using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace Lance.Client.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WindowPlacer
{
    private const int  PollAttempts   = 25;
    private const int  PollMs         = 200;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    internal static async Task PositionWindowAsync(int pid, int monX, int monY, int monWidth, int monHeight)
    {
        IntPtr hwnd = await PollWindowHandleAsync(pid);
        if (hwnd == IntPtr.Zero)
        {
            Log.Warning("Window placement: timed out waiting for Moonlight window (PID {Pid})", pid);
            return;
        }

        int x = monX;
        int y = monY;

        // Center the window on the target monitor if it fits; otherwise just move the origin.
        // Skipping centering for fullscreen-sized windows lets SDL handle fullscreen geometry.
        if (GetWindowRect(hwnd, out RECT rect))
        {
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < monWidth && h < monHeight)
            {
                x = monX + (monWidth - w) / 2;
                y = monY + (monHeight - h) / 2;
            }
        }

        if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
            Log.Warning("Window placement: SetWindowPos failed for PID {Pid}", pid);
        else
            Log.Debug("Window placement: PID {Pid} moved to ({X},{Y})", pid, x, y);
    }

    private static async Task<IntPtr> PollWindowHandleAsync(int pid)
    {
        for (int i = 0; i < PollAttempts; i++)
        {
            await Task.Delay(PollMs);
            try
            {
                using Process proc = Process.GetProcessById(pid);
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                    return hwnd;
            }
            catch (ArgumentException)
            {
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
