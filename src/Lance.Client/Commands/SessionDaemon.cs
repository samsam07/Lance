using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Client.Infrastructure;
using Lance.Hooks;
using Lance.Shared.Dtos;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Lance.Client.Commands;

// The foreground session daemon (Slice 6.6). `lance connect` blocks here until the
// session ends: it hands the agent the session id + monitor count, launches one
// Moonlight per returned slot inside a kill-on-close Job Object, runs session_started
// hooks, then watches the streams. When the last one exits or the user interrupts, it
// runs session_ended hooks, kills any survivors, and returns. The clean-disconnect ping
// is Slice 6.7; until then the agent detects teardown via probe-watch.
internal static class SessionDaemon
{
    public static async Task<int> RunAsync(
        ClientConfig config,
        string agentUrl,
        string? token,
        string sessionId,
        IReadOnlyList<MonitorInfo?> targetMonitors,
        string[] optionTokens,
        IReadOnlyList<HookFileRef> hookReferences,
        CancellationToken cancellationToken)
    {
        int timeout = config.Agent?.TimeoutSeconds ?? 30;
        using AgentClient client = new(agentUrl, timeout, token);

        AgentResult<SessionResponse> handshake = await client.CreateSessionAsync(sessionId, targetMonitors.Count, cancellationToken);
        if (handshake.IsUnreachable)
        {
            Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
            return ExitCodes.AgentUnreachable;
        }
        if (!handshake.IsSuccess)
        {
            Log.Error("Connect refused — {ErrorCode}: {ErrorMessage}", handshake.ErrorCode, handshake.ErrorMessage);
            return handshake.ErrorCode == "no_free_slots" ? ExitCodes.NoFreeSlots : ExitCodes.AgentError;
        }

        SlotDto[] slots = handshake.Value!.Slots;
        if (slots.Length == 0)
        {
            Log.Error("Agent accepted the session but returned no running slots.");
            return ExitCodes.AgentError;
        }

        Log.Information("Session {SessionId}: {Count} slot(s) ready.", sessionId, slots.Length);

        using JobObject job = new();
        List<LaunchedMoonlight> launched = LaunchMoonlights(config, slots, targetMonitors, optionTokens, job);
        if (launched.Count == 0)
        {
            // Degraded policy: nothing launched → fail, run no client hooks. The agent
            // times the session out (provision_timeout).
            Log.Error("No Moonlight instances launched — aborting the session.");
            return ExitCodes.MoonlightFailed;
        }

        await PositionWindowsAsync(launched);

        (HookDispatcher dispatcher, IReadOnlyList<LoadedHook> hooks) = LoadHooks(hookReferences);
        int[] slotIds = SlotIdsOf(launched);
        string agentIp = new Uri(agentUrl).Host;
        string clientIp = ResolveLocalIp(agentIp);

        Dictionary<string, string> startedEnv = BuildEnv(LanceEvents.SessionStarted, "explicit", sessionId, agentIp, clientIp, slotIds);
        Log.Information("Session {SessionId}: started ({Count} stream(s)); watching for disconnect. Press Ctrl+C to end.", sessionId, launched.Count);
        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, hooks, startedEnv, CancellationToken.None);

        string endedSource = await WaitForEndAsync(launched, cancellationToken);

        Dictionary<string, string> endedEnv = BuildEnv(LanceEvents.SessionEnded, endedSource, sessionId, agentIp, clientIp, slotIds);
        Log.Information("Session {SessionId}: ending ({Source}).", sessionId, endedSource);
        // Teardown must complete even after Ctrl-C, so it is not tied to the token.
        await dispatcher.DispatchAsync(LanceEvents.SessionEnded, hooks, endedEnv, CancellationToken.None);

        KillRemaining(launched);

        // Clean-disconnect ping (fast path). Best-effort — probe-watch backstops it.
        await client.DeleteSessionAsync(sessionId, CancellationToken.None);
        return ExitCodes.Success;
    }

    private static List<LaunchedMoonlight> LaunchMoonlights(
        ClientConfig config, SlotDto[] slots, IReadOnlyList<MonitorInfo?> targetMonitors, string[] optionTokens, JobObject job)
    {
        string executable = config.RemoteClient.Executable;
        string[] defaultFlags = config.RemoteClient.DefaultFlags;

        List<LaunchedMoonlight> launched = [];
        for (int i = 0; i < slots.Length; i++)
        {
            SlotDto slot = slots[i];
            MonitorInfo? monitor = i < targetMonitors.Count ? targetMonitors[i] : null;

            Process? process = TryLaunchMoonlight(executable, slot, monitor, defaultFlags, optionTokens);
            if (process is null)
            {
                Log.Warning("Failed to launch Moonlight for slot {Id} at {Host}:{Port}", slot.Id, slot.Host, slot.Port);
                continue;
            }

            job.Assign(process);
            launched.Add(new LaunchedMoonlight(process, slot, monitor));
            Log.Information("Moonlight launched for slot {Id} at {Host}:{Port} (PID {Pid})", slot.Id, slot.Host, slot.Port, process.Id);
        }

        return launched;
    }

    private static async Task PositionWindowsAsync(List<LaunchedMoonlight> launched)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Task> tasks = [];
        foreach (LaunchedMoonlight moonlight in launched)
        {
            if (moonlight.Monitor is MonitorInfo monitor)
            {
                tasks.Add(WindowPlacer.PositionWindowAsync(moonlight.Process.Id, monitor.X, monitor.Y, monitor.Width, monitor.Height));
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private static (HookDispatcher Dispatcher, IReadOnlyList<LoadedHook> Hooks) LoadHooks(IReadOnlyList<HookFileRef> references)
    {
        SerilogLoggerFactory loggerFactory = new(Log.Logger);
        HookLoader loader = new(loggerFactory.CreateLogger<HookLoader>());
        HookDispatcher dispatcher = new(new ProcessHookRunner(), loggerFactory.CreateLogger<HookDispatcher>());
        return (dispatcher, loader.Load(references));
    }

    private static async Task<string> WaitForEndAsync(List<LaunchedMoonlight> launched, CancellationToken cancellationToken)
    {
        Task allExited = Task.WhenAll(launched.ConvertAll(m => m.Process.WaitForExitAsync(CancellationToken.None)));
        Task interrupted = Task.Delay(Timeout.Infinite, cancellationToken);

        Task first = await Task.WhenAny(allExited, interrupted);
        return first == allExited ? "pid_watch" : "explicit";
    }

    private static void KillRemaining(List<LaunchedMoonlight> launched)
    {
        foreach (LaunchedMoonlight moonlight in launched)
        {
            try
            {
                if (!moonlight.Process.HasExited)
                {
                    moonlight.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not kill Moonlight PID {Pid}: {Reason}", moonlight.Process.Id, ex.Message);
            }

            moonlight.Process.Dispose();
        }
    }

    private static Process? TryLaunchMoonlight(string executable, SlotDto slot, MonitorInfo? monitor, string[] defaultFlags, string[] optionTokens)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("stream");
            startInfo.ArgumentList.Add($"{slot.Host}:{slot.Port}");
            startInfo.ArgumentList.Add("Desktop");

            foreach (string flag in defaultFlags)
            {
                startInfo.ArgumentList.Add(flag);
            }

            if (monitor is MonitorInfo resolution)
            {
                startInfo.ArgumentList.Add("--resolution");
                startInfo.ArgumentList.Add($"{resolution.Width}x{resolution.Height}");
            }

            foreach (string token in optionTokens)
            {
                startInfo.ArgumentList.Add(token);
            }

            Log.Debug("Launching: {Executable} {Args}", executable, string.Join(" ", startInfo.ArgumentList));
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log.Debug("Moonlight launch exception: {Reason}", ex.Message);
            return null;
        }
    }

    private static int[] SlotIdsOf(List<LaunchedMoonlight> launched)
    {
        int[] ids = new int[launched.Count];
        for (int i = 0; i < launched.Count; i++)
        {
            ids[i] = launched[i].Slot.Id;
        }

        return ids;
    }

    private static string ResolveLocalIp(string agentHost)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(agentHost, 9);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> BuildEnv(string eventName, string source, string sessionId, string agentIp, string clientIp, int[] slotIds)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LanceEnv.Event] = eventName,
            [LanceEnv.EventSource] = source,
            [LanceEnv.SessionId] = sessionId,
            [LanceEnv.Side] = "client",
            [LanceEnv.AgentIp] = agentIp,
            [LanceEnv.ClientIp] = clientIp,
            [LanceEnv.SlotIds] = string.Join(",", slotIds)
        };
    }

    private sealed record LaunchedMoonlight(Process Process, SlotDto Slot, MonitorInfo? Monitor);
}
