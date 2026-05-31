using System.Runtime.InteropServices;
using System.Text;

namespace Lance.Client.Infrastructure;

internal sealed record MonitorInfo
{
    public required int Id { get; init; }         // 1-indexed
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required bool IsPrimary { get; init; }
}

internal static class MonitorEnumerator
{
    internal static IReadOnlyList<MonitorInfo> Enumerate()
    {
        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return EnumerateLinux();
        }

        return [];
    }

    // — Windows ————————————————————————————————————————————

    private static IReadOnlyList<MonitorInfo> EnumerateWindows()
    {
        List<MonitorInfo> monitors = new();
        int index = 1;

        DISPLAY_DEVICE dd = new() { cb = DisplayDeviceCbSize };

        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DisplayDeviceActive) != 0)
            {
                DEVMODE dm = new() { dmSize = DevModeSize };

                if (EnumDisplaySettingsExW(dd.DeviceName, EnumCurrentSettings, ref dm, 0))
                {
                    monitors.Add(new MonitorInfo
                    {
                        Id = index++,
                        Name = dd.DeviceName,
                        Width = (int)dm.dmPelsWidth,
                        Height = (int)dm.dmPelsHeight,
                        X = dm.dmPositionX,
                        Y = dm.dmPositionY,
                        IsPrimary = (dd.StateFlags & DisplayDevicePrimary) != 0
                    });
                }
            }

            dd = new() { cb = DisplayDeviceCbSize };
        }

        return monitors;
    }

    private const uint DisplayDeviceActive = 0x00000001;
    private const uint DisplayDevicePrimary = 0x00000004;
    private const uint EnumCurrentSettings = unchecked((uint)-1);

    // DISPLAY_DEVICE total: 4 + (32×2) + (128×2) + 4 + (128×2) + (128×2) = 840 bytes
    private const uint DisplayDeviceCbSize = 840u;

    // DEVMODEW total size as documented for Windows XP+
    private const short DevModeSize = 220;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    // Only the fields needed; Size = 220 ensures correct struct footprint for marshaling.
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
    private struct DEVMODE
    {
        [FieldOffset(68)] public short dmSize;
        [FieldOffset(72)] public uint dmFields;
        [FieldOffset(76)] public int dmPositionX;   // POINTL.x in display union
        [FieldOffset(80)] public int dmPositionY;   // POINTL.y
        [FieldOffset(172)] public uint dmPelsWidth;
        [FieldOffset(176)] public uint dmPelsHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(
        string lpszDeviceName,
        uint iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);

    // — Linux (Xrandr 1.5, x64 only) ——————————————————————
    //
    // Uses libX11 + libXrandr. Requires X11 or XWayland. Pure Wayland without
    // XWayland is not supported yet — returns an empty list in that case.
    // A more robust approach covering both X11 and native Wayland is planned
    // for a later phase.

    private static IReadOnlyList<MonitorInfo> EnumerateLinux()
    {
        // XRRMonitorInfo layout is x64-specific (Atom = 8 bytes, pointer = 8 bytes)
        if (IntPtr.Size != 8)
        {
            return [];
        }

        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            IntPtr monitorsPtr = XRRGetMonitors(display, root, true, out int count);

            if (monitorsPtr == IntPtr.Zero || count <= 0)
            {
                return [];
            }

            try
            {
                List<MonitorInfo> monitors = new();
                int structSize = XRRMonitorInfoSize;

                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = IntPtr.Add(monitorsPtr, i * structSize);
                    XRRMonitorInfo info = Marshal.PtrToStructure<XRRMonitorInfo>(itemPtr);

                    string name = GetAtomName(display, info.Name);
                    monitors.Add(new MonitorInfo
                    {
                        Id = i + 1,
                        Name = name,
                        Width = info.Width,
                        Height = info.Height,
                        X = info.X,
                        Y = info.Y,
                        IsPrimary = info.Primary != 0
                    });
                }

                return monitors;
            }
            finally
            {
                XRRFreeMonitors(monitorsPtr);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static string GetAtomName(IntPtr display, IntPtr atom)
    {
        IntPtr namePtr = XGetAtomName(display, atom);
        if (namePtr == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
        }
        finally
        {
            XFree(namePtr);
        }
    }

    // XRRMonitorInfo layout (x64 Linux):
    // Atom name      @ 0  (IntPtr = 8 bytes)
    // Bool primary   @ 8  (int    = 4 bytes)
    // Bool automatic @ 12 (int    = 4 bytes)
    // int noutput    @ 16
    // int x          @ 20
    // int y          @ 24
    // int width      @ 28 (pixels)
    // int height     @ 32 (pixels)
    // int mwidth     @ 36 (mm)
    // int mheight    @ 40 (mm)
    // [4 bytes pad]
    // RROutput* out  @ 48 (IntPtr = 8 bytes)
    // Total: 56 bytes
    private const int XRRMonitorInfoSize = 56;

    [StructLayout(LayoutKind.Explicit)]
    private struct XRRMonitorInfo
    {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public int Primary;
        [FieldOffset(12)] public int Automatic;
        [FieldOffset(16)] public int NOutput;
        [FieldOffset(20)] public int X;
        [FieldOffset(24)] public int Y;
        [FieldOffset(28)] public int Width;
        [FieldOffset(32)] public int Height;
        [FieldOffset(36)] public int MWidth;
        [FieldOffset(40)] public int MHeight;
        [FieldOffset(48)] public IntPtr Outputs;
    }

    [DllImport("libX11")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11")]
    private static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);

    [DllImport("libX11")]
    private static extern int XFree(IntPtr data);

    [DllImport("libXrandr")]
    private static extern IntPtr XRRGetMonitors(
        IntPtr display, IntPtr window, bool getActive, out int nmonitors);

    [DllImport("libXrandr")]
    private static extern void XRRFreeMonitors(IntPtr monitors);
}
