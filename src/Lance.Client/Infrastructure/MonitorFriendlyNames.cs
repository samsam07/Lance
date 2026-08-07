using System.Runtime.InteropServices;

namespace Lance.Client.Infrastructure;

// Maps a GDI display name (`\\.\DISPLAY1`) to the monitor's EDID friendly name
// ("GW2480"), so a monitor can be referred to by something a person recognises.
//
// Windows' Connecting and Configuring Displays (CCD) API is the only P/Invoke-only
// route to those names: WMI would serve them too, but `System.Management` is not
// AOT-safe and this project publishes AOT. Every failure degrades to "no friendly
// name", which simply means that monitor can still be addressed by id.
internal static class MonitorFriendlyNames
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetTargetName = 2;
    private const int ErrorSuccess = 0;

    public static IReadOnlyDictionary<string, string> ByDeviceName()
    {
        Dictionary<string, string> friendlyByDevice = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            Collect(friendlyByDevice);
        }
        catch (Exception)
        {
            // A missing export or an unexpected layout must not stop `lance monitors`
            // or a connect; ids keep working without names.
        }

        return friendlyByDevice;
    }

    private static void Collect(Dictionary<string, string> friendlyByDevice)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess)
        {
            return;
        }

        DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ErrorSuccess)
        {
            return;
        }

        for (int i = 0; i < pathCount; i++)
        {
            string? deviceName = TryReadSourceName(paths[i]);
            string? friendlyName = TryReadTargetName(paths[i]);

            if (!string.IsNullOrWhiteSpace(deviceName) && !string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyByDevice[deviceName] = friendlyName.Trim();
            }
        }
    }

    private static string? TryReadSourceName(DISPLAYCONFIG_PATH_INFO path)
    {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME request = new()
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DeviceInfoGetSourceName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ErrorSuccess ? request.viewGdiDeviceName : null;
    }

    private static string? TryReadTargetName(DISPLAYCONFIG_PATH_INFO path)
    {
        DISPLAYCONFIG_TARGET_DEVICE_NAME request = new()
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DeviceInfoGetTargetName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ErrorSuccess ? request.monitorFriendlyDeviceName : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // The trailing 48 bytes are a union Lance never reads; Size pins the array stride.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
