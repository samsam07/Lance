using System.CommandLine;
using System.Diagnostics;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Client.Infrastructure;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

// Session-based disconnect (Slice 6.7). Kills a session's Moonlights (which makes the
// blocking `lance connect` daemon tear down) and sends the clean-disconnect ping so the
// agent ends its side immediately. The agent is the fast path for resolving a session's
// slots; explicit host:port args are the degraded path when the agent is unreachable.
internal static class DisconnectCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Option<string?> sessionIdOption = new("--session-id")
        {
            Description = "Session id to disconnect (default: all active sessions)"
        };
        Option<bool> keepRunningOption = new("--keep-running")
        {
            Description = "Leave Apollo running for a fast reconnect (default)"
        };
        Option<bool> purgeOption = new("--purge")
        {
            Description = "Also stop and deallocate the session's slots (Slot 0 excluded)"
        };
        Argument<string[]> hostPortsArgument = new("host:port")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Explicit host:port targets — the fallback used when the agent is unreachable"
        };

        Command command = new("disconnect", "End a session: kill its Moonlights and tell the agent to tear down");
        command.Add(sessionIdOption);
        command.Add(keepRunningOption);
        command.Add(purgeOption);
        command.Add(hostPortsArgument);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            bool purge = pr.GetValue(purgeOption);
            if (purge && pr.GetValue(keepRunningOption))
            {
                Log.Warning("--keep-running is ignored when --purge is specified");
            }

            string? sessionId = pr.GetValue(sessionIdOption);
            string[] hostPorts = pr.GetValue(hostPortsArgument) ?? [];

            ClientConfig? config = globals.GetConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, globals, config);
            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            int timeout = config?.Agent?.TimeoutSeconds ?? 30;
            string? token = CommandHelpers.ResolveToken(pr, globals, config);
            string executableName = config?.RemoteClient.Executable ?? "moonlight";

            using AgentClient client = new(agentUrl, timeout, token);

            (List<DisconnectTarget>? targets, int failureExit) = await ResolveTargetsAsync(client, sessionId, hostPorts, ct);
            if (targets is null)
            {
                return failureExit;
            }
            if (targets.Count == 0)
            {
                Log.Information("No active sessions to disconnect.");
                return ExitCodes.Success;
            }

            IReadOnlyList<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
                ProcessCommandLine.FindMoonlightProcesses(executableName);

            foreach (DisconnectTarget target in targets)
            {
                await DisconnectOneAsync(client, target, moonlights, purge, ct);
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private static async Task<(List<DisconnectTarget>? Targets, int FailureExit)> ResolveTargetsAsync(
        AgentClient client, string? sessionId, string[] hostPorts, CancellationToken ct)
    {
        if (sessionId is not null)
        {
            AgentResult<SessionResponse> result = await client.GetSessionAsync(sessionId, ct);
            if (result.IsSuccess)
            {
                return ([new DisconnectTarget(sessionId, SlotRefsFrom(result.Value!.Slots))], 0);
            }
            if (result.IsUnreachable)
            {
                return FallbackOrUnreachable(hostPorts);
            }

            Log.Information("Session '{SessionId}' is not active.", sessionId);
            return (hostPorts.Length > 0 ? [FallbackTarget(hostPorts)] : [], 0);
        }

        AgentResult<SessionsListResponse> list = await client.GetSessionsAsync(ct);
        if (list.IsSuccess)
        {
            List<DisconnectTarget> targets = [];
            foreach (SessionResponse session in list.Value!.Sessions)
            {
                targets.Add(new DisconnectTarget(session.SessionId, SlotRefsFrom(session.Slots)));
            }
            return (targets, 0);
        }
        if (list.IsUnreachable)
        {
            return FallbackOrUnreachable(hostPorts);
        }

        Log.Error("Agent returned error {ErrorCode}: {ErrorMessage}", list.ErrorCode, list.ErrorMessage);
        return (null, ExitCodes.AgentError);
    }

    private static (List<DisconnectTarget>? Targets, int FailureExit) FallbackOrUnreachable(string[] hostPorts)
    {
        if (hostPorts.Length > 0)
        {
            Log.Warning("Agent unreachable — killing Moonlights by the given host:port only (no agent teardown).");
            return ([FallbackTarget(hostPorts)], 0);
        }

        Log.Error("Agent unreachable and no host:port fallback given.");
        return (null, ExitCodes.AgentUnreachable);
    }

    private static async Task DisconnectOneAsync(
        AgentClient client, DisconnectTarget target,
        IReadOnlyList<(int Pid, string CommandLine, string? WindowTitle)> moonlights, bool purge, CancellationToken ct)
    {
        KillMoonlights(target, moonlights);

        if (target.SessionId is not null)
        {
            await client.DeleteSessionAsync(target.SessionId, ct);
            Log.Information("Session {SessionId}: disconnect signalled to the agent.", target.SessionId);

            if (purge)
            {
                await PurgeSlotsAsync(client, target, ct);
            }
        }
    }

    private static void KillMoonlights(DisconnectTarget target, IReadOnlyList<(int Pid, string CommandLine, string? WindowTitle)> moonlights)
    {
        HashSet<int> killed = [];
        foreach (SlotRef slot in target.Slots)
        {
            foreach (int pid in ProcessCommandLine.FindMoonlightsForSlot(moonlights, slot.HostPort, slot.Name))
            {
                if (!killed.Add(pid))
                {
                    continue;
                }

                try
                {
                    using Process process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    Log.Information("Killed Moonlight process {Pid} ({HostPort}).", pid, slot.HostPort);
                }
                catch (Exception ex)
                {
                    Log.Warning("Could not kill Moonlight process {Pid}: {Reason}", pid, ex.Message);
                }
            }
        }
    }

    private static async Task PurgeSlotsAsync(AgentClient client, DisconnectTarget target, CancellationToken ct)
    {
        foreach (SlotRef slot in target.Slots)
        {
            if (slot.Id <= 0)
            {
                continue;   // Slot 0 (template) and fallback pseudo-slots (-1) are never purged
            }

            await client.StopSlotAsync(slot.Id, ct);
            AgentResult<bool> dealloc = await client.DeallocateSlotAsync(slot.Id, ct);
            if (dealloc.IsSuccess)
            {
                Log.Information("Slot {Id}: stopped and deallocated.", slot.Id);
            }
            else if (!dealloc.IsUnreachable)
            {
                Log.Warning("Slot {Id}: deallocation failed — {ErrorCode}: {ErrorMessage}", slot.Id, dealloc.ErrorCode, dealloc.ErrorMessage);
            }
        }
    }

    private static List<SlotRef> SlotRefsFrom(SlotDto[] slots)
    {
        List<SlotRef> refs = [];
        foreach (SlotDto slot in slots)
        {
            refs.Add(new SlotRef(slot.Id, $"{slot.Host}:{slot.Port}", slot.Name));
        }

        return refs;
    }

    private static DisconnectTarget FallbackTarget(string[] hostPorts)
    {
        List<SlotRef> refs = [];
        foreach (string hostPort in hostPorts)
        {
            refs.Add(new SlotRef(-1, hostPort, string.Empty));
        }

        return new DisconnectTarget(null, refs);
    }

    private sealed record DisconnectTarget(string? SessionId, IReadOnlyList<SlotRef> Slots);

    private sealed record SlotRef(int Id, string HostPort, string Name);
}
