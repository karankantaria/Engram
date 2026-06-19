using System.Runtime.InteropServices;

namespace Engram;

/// <summary>
/// A borderless (WindowStyle=None) window maximizes to the full monitor by
/// default, covering the taskbar and bleeding off-screen. This handles
/// WM_GETMINMAXINFO to clamp the maximized bounds to the monitor's work area
/// (the screen minus the taskbar), so "maximize" reliably fits any display.
/// </summary>
internal static class MaximizeToWorkArea
{
    public const int WM_GETMINMAXINFO = 0x0024;

    public static void Handle(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = mi.rcWork;
        var full = mi.rcMonitor;

        // Position/size are relative to the monitor's top-left.
        mmi.ptMaxPosition.X = work.Left - full.Left;
        mmi.ptMaxPosition.Y = work.Top - full.Top;
        mmi.ptMaxSize.X = work.Right - work.Left;
        mmi.ptMaxSize.Y = work.Bottom - work.Top;
        mmi.ptMaxTrackSize.X = work.Right - work.Left;
        mmi.ptMaxTrackSize.Y = work.Bottom - work.Top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
