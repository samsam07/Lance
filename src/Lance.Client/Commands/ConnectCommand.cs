using System.CommandLine;
using System.Diagnostics;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class ConnectCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Func<ClientConfig?> getConfig)
    {
        Option<int> countOption = new("--count") { Description = "Number of monitors to connect" };

        Command command = new("connect", "Allocate and start slots, then launch one Moonlight per monitor");
        command.Add(countOption);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int count = pr.GetValue(countOption);
            if (count < 1)
            {
                Log.Error("--count must be at least 1");
                return ExitCodes.Generic;
            }

            ClientConfig? config = getConfig();
            if (config is null)
            {
                Log.Error("Config could not be loaded — provide --config <path> or place lance.json beside the executable");
                return ExitCodes.ConfigResolutionFailed;
            }

            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, agentOption, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            string executable = config.RemoteClient.Executable;
            string[] defaultFlags = config.RemoteClient.DefaultFlags;
            int timeout = config.Agent?.TimeoutSeconds ?? 30;

            Log.Debug("Effective config — executable: {Executable}, flags: [{Flags}], timeout: {Timeout}s",
                executable, string.Join(", ", defaultFlags), timeout);

            using AgentClient client = new(agentUrl, timeout);

            Log.Information("Allocating {Count} slot(s)", count);
            AgentResult<SlotsResponse> allocResult = await client.AllocateSlotsAsync(count, ct);

            if (allocResult.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return ExitCodes.AgentUnreachable;
            }

            if (!allocResult.IsSuccess)
            {
                Log.Error("Allocation failed — {ErrorCode}: {ErrorMessage}", allocResult.ErrorCode, allocResult.ErrorMessage);
                return ExitCodes.AgentError;
            }

            Dictionary<int, SlotDto> slotById = [];
            foreach (SlotDto s in allocResult.Value!.Slots)
                slotById[s.Id] = s;

            List<SlotDto> startedSlots = [];
            for (int id = 0; id < count; id++)
            {
                if (!slotById.TryGetValue(id, out SlotDto? slot))
                {
                    Log.Warning("Slot {Id} missing from allocate response — skipping", id);
                    continue;
                }

                AgentResult<bool> startResult = await client.StartSlotAsync(id, ct);

                if (startResult.IsUnreachable)
                {
                    Log.Warning("Agent unreachable while starting slot {Id} — skipping", id);
                    continue;
                }

                if (!startResult.IsSuccess)
                {
                    Log.Warning("Slot {Id} failed to start — {ErrorCode}: {ErrorMessage}", id, startResult.ErrorCode, startResult.ErrorMessage);
                    continue;
                }

                startedSlots.Add(slot);
                Log.Information("Slot {Id} started at {Host}:{Port}", id, slot.Host, slot.Port);
            }

            if (startedSlots.Count == 0)
            {
                Log.Error("All {Count} slot(s) failed to start — no monitors connected", count);
                return ExitCodes.Generic;
            }

            int launched = 0;
            foreach (SlotDto slot in startedSlots)
            {
                if (TryLaunchMoonlight(executable, slot, defaultFlags, out int pid))
                {
                    Log.Information("Moonlight launched for slot {Id} at {Host}:{Port} (PID {Pid})", slot.Id, slot.Host, slot.Port, pid);
                    launched++;
                }
                else
                {
                    Log.Warning("Failed to launch Moonlight for slot {Id} at {Host}:{Port}", slot.Id, slot.Host, slot.Port);
                }
            }

            if (launched == 0)
            {
                Log.Error("All Moonlight launches failed");
                return ExitCodes.MoonlightFailed;
            }

            Log.Information("Connect complete — {Launched}/{Total} monitor(s) connected", launched, count);
            return ExitCodes.Success;
        });

        return command;
    }

    private static bool TryLaunchMoonlight(string executable, SlotDto slot, string[] defaultFlags, out int pid)
    {
        pid = 0;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            psi.ArgumentList.Add("stream");
            psi.ArgumentList.Add($"{slot.Host}:{slot.Port}");
            psi.ArgumentList.Add("Desktop");
            foreach (string flag in defaultFlags)
                psi.ArgumentList.Add(flag);

            Log.Debug("Launching: {Executable} {Args}", executable, string.Join(" ", psi.ArgumentList));
            Process? process = Process.Start(psi);
            if (process is null)
                return false;

            pid = process.Id;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("Moonlight launch exception: {Reason}", ex.Message);
            return false;
        }
    }
}
