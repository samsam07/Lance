using System.CommandLine;
using System.Diagnostics;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Client.Infrastructure;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class ConnectCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Option<string?> monitorsOption = new("--monitors")
        {
            Description = "Comma-separated 1-indexed monitor IDs to connect (default: all physical monitors)"
        };
        Option<string?> optionsOption = new("--options")
        {
            Description = "Extra Moonlight flags appended to each launch (e.g. \"--bitrate 80000 --fps 120\")"
        };

        Command command = new("connect", "Allocate and start slots, then launch one Moonlight per monitor");
        command.Add(monitorsOption);
        command.Add(optionsOption);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            ClientConfig? config = globals.GetConfig();
            if (config is null)
            {
                Log.Error("Config could not be loaded — provide --config <path> or place lance.json beside the executable");
                return ExitCodes.ConfigResolutionFailed;
            }

            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, globals, config);
            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            // Resolve target monitors → ordered list; position i drives slot i's resolution
            string? monitorsStr = pr.GetValue(monitorsOption);
            IReadOnlyList<MonitorInfo> allMonitors = MonitorEnumerator.Enumerate();
            List<int> targetMonitorIds = new();

            if (monitorsStr is null)
            {
                if (allMonitors.Count == 0)
                {
                    Log.Error("Monitor detection failed. Use --monitors <list> to connect manually.");
                    return ExitCodes.Generic;
                }
                foreach (MonitorInfo m in allMonitors)
                    targetMonitorIds.Add(m.Id);
            }
            else
            {
                bool canValidate = allMonitors.Count > 0;
                HashSet<int> validIds = new();
                foreach (MonitorInfo m in allMonitors)
                    validIds.Add(m.Id);

                HashSet<int> seen = new();
                foreach (string part in monitorsStr.Split(','))
                {
                    string trimmed = part.Trim();
                    if (!int.TryParse(trimmed, out int id))
                    {
                        Log.Warning("Skipping invalid monitor ID '{Id}'", trimmed);
                        continue;
                    }
                    if (!seen.Add(id))
                    {
                        Log.Error("Duplicate monitor ID {Id} in --monitors list", id);
                        return ExitCodes.Generic;
                    }
                    if (canValidate && !validIds.Contains(id))
                    {
                        Log.Warning("Monitor {Id} not found on this machine — skipping", id);
                        continue;
                    }
                    targetMonitorIds.Add(id);
                }

                if (targetMonitorIds.Count == 0)
                {
                    Log.Error("No valid monitor IDs in --monitors list");
                    return ExitCodes.Generic;
                }
            }

            Dictionary<int, MonitorInfo> monitorById = [];
            foreach (MonitorInfo m in allMonitors)
                monitorById[m.Id] = m;

            int N = targetMonitorIds.Count;
            Log.Information("Connecting {N} monitor(s)", N);

            string executable = config.RemoteClient.Executable;
            string[] defaultFlags = config.RemoteClient.DefaultFlags;
            int timeout = config.Agent?.TimeoutSeconds ?? 30;
            string? token = CommandHelpers.ResolveToken(pr, globals, config);

            string? optionsStr = pr.GetValue(optionsOption);
            string[] optionTokens = optionsStr is null
                ? []
                : optionsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Log.Debug("Effective config — executable: {Executable}, flags: [{Flags}], timeout: {Timeout}s",
                executable, string.Join(", ", defaultFlags), timeout);

            using AgentClient client = new(agentUrl, timeout, token);

            // Free-slot check: GET /health + GET /slots
            int maxSlots = int.MaxValue;
            AgentResult<HealthResponse> healthResult = await client.GetHealthAsync(ct);
            if (healthResult.IsSuccess)
            {
                maxSlots = healthResult.Value!.MaxSlots;
            }

            AgentResult<SlotsResponse> slotsResult = await client.GetSlotsAsync(ct);
            if (slotsResult.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return ExitCodes.AgentUnreachable;
            }
            if (!slotsResult.IsSuccess)
            {
                Log.Error("Agent returned error {ErrorCode}: {ErrorMessage}", slotsResult.ErrorCode, slotsResult.ErrorMessage);
                return ExitCodes.AgentError;
            }

            int freeCount = 0;
            int totalCount = 0;
            foreach (SlotDto slot in slotsResult.Value!.Slots)
            {
                totalCount++;
                if (slot.Status != "Connected")
                    freeCount++;
            }

            int availableCapacity = freeCount + (maxSlots - totalCount);
            if (N > availableCapacity)
            {
                Log.Error(
                    "No capacity for {N} monitor(s): {Free} free slot(s), {Total}/{Max} used. Disconnect first.",
                    N, freeCount, totalCount, maxSlots);
                return ExitCodes.NoFreeSlots;
            }

            // Allocate to reach N slots in the pool
            Log.Information("Allocating {Count} slot(s)", N);
            AgentResult<SlotsResponse> allocResult = await client.AllocateSlotsAsync(N, ct);

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

            // Phase A — ensure each target slot is up (start if Allocated, reuse if already Running/Connected)
            List<(SlotDto Slot, MonitorInfo? Monitor)> upSlots = [];
            for (int id = 0; id < N; id++)
            {
                if (!slotById.TryGetValue(id, out SlotDto? slot))
                {
                    Log.Warning("Slot {Id} missing from allocate response — skipping", id);
                    continue;
                }

                monitorById.TryGetValue(targetMonitorIds[id], out MonitorInfo? monitor);

                if (slot.Status == "Allocated")
                {
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
                    Log.Information("Slot {Id} started at {Host}:{Port}", id, slot.Host, slot.Port);
                }
                else
                {
                    Log.Debug("Slot {Id} already {Status} — reusing", id, slot.Status);
                }

                upSlots.Add((slot, monitor));
            }

            if (upSlots.Count == 0)
            {
                Log.Error("No slots came up — no monitors connected");
                return ExitCodes.Generic;
            }

            // Phase B — launch Moonlight for each up slot that has no live local Moonlight
            IReadOnlyList<(int Pid, string CommandLine)> moonlights =
                ProcessCommandLine.FindMoonlightProcesses(executable);

            int launched = 0;
            int reused = 0;
            int attempted = 0;
            foreach ((SlotDto slot, MonitorInfo? monitor) in upSlots)
            {
                string hostPort = $"{slot.Host}:{slot.Port}";
                int existingPid = FindMoonlightFor(moonlights, hostPort);
                if (existingPid != 0)
                {
                    Log.Information("Slot {Id} already has Moonlight (PID {Pid}) — skipping launch", slot.Id, existingPid);
                    reused++;
                    continue;
                }

                attempted++;
                if (TryLaunchMoonlight(executable, slot, monitor, defaultFlags, optionTokens, out int pid))
                {
                    Log.Information("Moonlight launched for slot {Id} at {Host}:{Port} (PID {Pid})", slot.Id, slot.Host, slot.Port, pid);
                    launched++;
                }
                else
                {
                    Log.Warning("Failed to launch Moonlight for slot {Id} at {Host}:{Port}", slot.Id, slot.Host, slot.Port);
                }
            }

            if (attempted > 0 && launched == 0)
            {
                Log.Error("All Moonlight launches failed");
                return ExitCodes.MoonlightFailed;
            }

            Log.Information("Connect complete — {Up} slot(s) up ({Launched} launched, {Reused} already running)",
                upSlots.Count, launched, reused);
            return ExitCodes.Success;
        });

        return command;
    }

    private static int FindMoonlightFor(IReadOnlyList<(int Pid, string CommandLine)> moonlights, string hostPort)
    {
        foreach ((int Pid, string CommandLine) m in moonlights)
        {
            if (m.CommandLine.Contains(hostPort, StringComparison.OrdinalIgnoreCase))
            {
                return m.Pid;
            }
        }
        return 0;
    }

    private static bool TryLaunchMoonlight(
        string executable, SlotDto slot, MonitorInfo? monitor,
        string[] defaultFlags, string[] optionTokens, out int pid)
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

            // Per-monitor resolution overrides the config default (Moonlight uses the last value)
            if (monitor is not null)
            {
                psi.ArgumentList.Add("--resolution");
                psi.ArgumentList.Add($"{monitor.Width}x{monitor.Height}");
            }

            // CLI --options win over everything else (appended last)
            foreach (string token in optionTokens)
                psi.ArgumentList.Add(token);

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
