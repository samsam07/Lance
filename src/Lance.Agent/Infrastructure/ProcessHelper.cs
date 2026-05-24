using System.Diagnostics;

namespace Lance.Agent.Infrastructure;

internal static class ProcessHelper
{
    public static bool IsAlive(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
