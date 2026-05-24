using System.Security.Principal;

namespace Lance.Agent.Infrastructure;

internal static class AdminGuard
{
    public static void RequireElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);

        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Lance Agent must be run as Administrator on Windows.");
        }
    }
}
