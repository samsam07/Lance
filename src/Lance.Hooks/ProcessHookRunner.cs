using System.ComponentModel;
using System.Diagnostics;

namespace Lance.Hooks;

// The real process runner: launches each command directly (no shell) via
// ProcessStartInfo.ArgumentList, with the event payload injected as environment
// variables. Lance's only relationship with a spawned process is optionally waiting
// for a synchronous command's exit; it never kills or otherwise supervises them.
public sealed class ProcessHookRunner : IHookProcessRunner
{
    public async Task<HookRunResult> RunAndWaitAsync(HookProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = StartProcess(spec);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The process could not be launched at all (e.g. the executable is missing).
            // A hook command's failure must never take down the caller, so report it.
            return new HookRunResult { TimedOut = false, LaunchError = ex.Message };
        }

        using (process)
        using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
                return new HookRunResult { TimedOut = false, ExitCode = process.ExitCode };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out: stop waiting but leave the process running — its lifecycle is
                // the tool's, not Lance's. The engine applies the command's onError policy.
                return new HookRunResult { TimedOut = true };
            }
        }
    }

    public string? Start(HookProcessSpec spec)
    {
        try
        {
            // Fire-and-forget: releasing our handle does not stop the process.
            using Process process = StartProcess(spec);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private static Process StartProcess(HookProcessSpec spec)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = spec.Command,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false
        };

        foreach (string arg in spec.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (KeyValuePair<string, string> variable in spec.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start hook process '{spec.Command}'.");
        }

        return process;
    }
}
