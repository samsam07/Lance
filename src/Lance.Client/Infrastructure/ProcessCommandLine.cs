using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lance.Client.Infrastructure;

internal static class ProcessCommandLine
{
    internal static IReadOnlyList<(int Pid, string CommandLine)> FindMoonlightProcesses(string executableName)
    {
        string procName = Path.GetFileNameWithoutExtension(executableName);
        Process[] processes = Process.GetProcessesByName(procName);
        List<(int, string)> results = new();

        foreach (Process proc in processes)
        {
            int pid = proc.Id;
            proc.Dispose();

            string? cmdLine = Read(pid);
            if (cmdLine is not null)
            {
                results.Add((pid, cmdLine));
            }
        }

        return results;
    }

    internal static string? Read(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(pid);
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(pid);
        }

        return null;
    }

    private static string? ReadLinux(int pid)
    {
        string path = $"/proc/{pid}/cmdline";
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string raw = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            // /proc cmdline uses null bytes as arg separators — normalize to spaces for substring matching
            return raw.Length == 0 ? null : raw.Replace('\0', ' ');
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadWindows(int pid)
    {
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmRead = 0x0010;

        IntPtr hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            PROCESS_BASIC_INFORMATION pbi = default;
            int status = NtQueryInformationProcess(
                hProcess, 0, ref pbi, 6 * IntPtr.Size, out _);

            if (status != 0)
                return null;

            // PEB.ProcessParameters pointer: x64 → offset 0x20, x86 → offset 0x10
            int ptrSize = IntPtr.Size;
            int ppOffset = ptrSize == 8 ? 0x20 : 0x10;

            byte[] ppBuf = new byte[ptrSize];
            if (!ReadProcessMemory(hProcess, IntPtr.Add(pbi.PebBaseAddress, ppOffset),
                    ppBuf, ptrSize, out _))
                return null;

            IntPtr ppPtr = ptrSize == 8
                ? new IntPtr(BitConverter.ToInt64(ppBuf, 0))
                : new IntPtr(BitConverter.ToInt32(ppBuf, 0));

            // RTL_USER_PROCESS_PARAMETERS.CommandLine UNICODE_STRING:
            // x64 → offset 0x70; layout: Length(2) + MaxLength(2) + pad(4) + Buffer(8)
            // x86 → offset 0x40; layout: Length(2) + MaxLength(2) + Buffer(4)
            int cmdOffset = ptrSize == 8 ? 0x70 : 0x40;
            int usSize = ptrSize == 8 ? 16 : 8;

            byte[] usBuf = new byte[usSize];
            if (!ReadProcessMemory(hProcess, IntPtr.Add(ppPtr, cmdOffset),
                    usBuf, usSize, out _))
                return null;

            ushort byteLen = BitConverter.ToUInt16(usBuf, 0);
            if (byteLen == 0)
                return null;

            int bufPtrOffset = ptrSize == 8 ? 8 : 4;
            IntPtr bufPtr = ptrSize == 8
                ? new IntPtr(BitConverter.ToInt64(usBuf, bufPtrOffset))
                : new IntPtr(BitConverter.ToInt32(usBuf, bufPtrOffset));

            byte[] chars = new byte[byteLen];
            if (!ReadProcessMemory(hProcess, bufPtr, chars, byteLen, out _))
                return null;

            return Encoding.Unicode.GetString(chars);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int nSize,
        out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
