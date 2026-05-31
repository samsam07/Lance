using System.CommandLine;
using System.Diagnostics;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Client.Infrastructure;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class DisconnectCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Option<string?> slotsOption = new("--slots")
        {
            Description = "Comma-separated slot IDs to disconnect (default: all Running/Connected slots)"
        };
        Option<bool> keepRunningOption = new("--keep-running")
        {
            Description = "Skip stopping Apollo on the agent; Moonlight is still killed"
        };
        Option<bool> purgeOption = new("--purge")
        {
            Description = "Stop, kill Moonlight, then deallocate the slot (Slot 0 excluded)"
        };

        Command command = new("disconnect", "Stop Moonlight and optionally stop Apollo for target slots");
        command.Add(slotsOption);
        command.Add(keepRunningOption);
        command.Add(purgeOption);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            bool keepRunning = pr.GetValue(keepRunningOption);
            bool purge = pr.GetValue(purgeOption);

            if (keepRunning && purge)
            {
                Log.Warning("--keep-running is ignored when --purge is specified");
                keepRunning = false;
            }

            ClientConfig? config = globals.GetConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, globals, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            int timeout = config?.Agent?.TimeoutSeconds ?? 30;
            string? token = CommandHelpers.ResolveToken(pr, globals, config);
            string executableName = config?.RemoteClient.Executable ?? "moonlight";

            using AgentClient client = new(agentUrl, timeout, token);
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

            // Determine target slots
            string? slotsStr = pr.GetValue(slotsOption);
            List<SlotDto> targets = new();

            if (slotsStr is null)
            {
                foreach (SlotDto s in slotsResult.Value!.Slots)
                {
                    if (s.Status == "Running" || s.Status == "Connected")
                    {
                        targets.Add(s);
                    }
                }
            }
            else
            {
                Dictionary<int, SlotDto> byId = new();
                foreach (SlotDto s in slotsResult.Value!.Slots)
                    byId[s.Id] = s;

                foreach (string part in slotsStr.Split(','))
                {
                    if (!int.TryParse(part.Trim(), out int id))
                    {
                        Log.Warning("Skipping invalid slot ID '{Part}'", part.Trim());
                        continue;
                    }
                    if (!byId.TryGetValue(id, out SlotDto? slot))
                    {
                        Log.Warning("Slot {Id} not found — skipping", id);
                        continue;
                    }
                    targets.Add(slot);
                }
            }

            if (targets.Count == 0)
            {
                Log.Information("No target slots to disconnect");
                return ExitCodes.Success;
            }

            IReadOnlyList<(int Pid, string CommandLine)> moonlights =
                ProcessCommandLine.FindMoonlightProcesses(executableName);

            int disconnected = 0;
            foreach (SlotDto slot in targets)
            {
                Log.Debug("Disconnecting slot {Id} ({Name})", slot.Id, slot.Name);

                // 1. Kill matching Moonlight process (always, regardless of flags)
                foreach ((int Pid, string CommandLine) moonlight in moonlights)
                {
                    string hostPort = $"{slot.Host}:{slot.Port}";
                    if (moonlight.CommandLine.Contains(hostPort, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using Process p = Process.GetProcessById(moonlight.Pid);
                            p.Kill();
                            Log.Information("Slot {Id}: Moonlight process {Pid} killed", slot.Id, moonlight.Pid);
                        }
                        catch
                        {
                            Log.Warning("Slot {Id}: could not kill Moonlight process {Pid}", slot.Id, moonlight.Pid);
                        }
                        break;
                    }
                }

                // 2. Stop Apollo on the agent (unless --keep-running)
                if (!keepRunning)
                {
                    AgentResult<bool> stopResult = await client.StopSlotAsync(slot.Id, ct);
                    if (stopResult.IsUnreachable)
                    {
                        Log.Warning("Slot {Id}: agent unreachable while stopping", slot.Id);
                    }
                    else if (!stopResult.IsSuccess)
                    {
                        Log.Warning("Slot {Id}: stop failed — {ErrorCode}: {ErrorMessage}",
                            slot.Id, stopResult.ErrorCode, stopResult.ErrorMessage);
                    }
                    else
                    {
                        Log.Information("Slot {Id}: Apollo stopped", slot.Id);
                    }
                }

                // 3. Deallocate (--purge only; Slot 0 excluded)
                if (purge && slot.Id != 0)
                {
                    AgentResult<bool> deallocResult = await client.DeallocateSlotAsync(slot.Id, ct);
                    if (!deallocResult.IsSuccess && !deallocResult.IsUnreachable)
                    {
                        Log.Warning("Slot {Id}: deallocation failed — {ErrorCode}: {ErrorMessage}",
                            slot.Id, deallocResult.ErrorCode, deallocResult.ErrorMessage);
                    }
                    else if (deallocResult.IsSuccess)
                    {
                        Log.Information("Slot {Id}: deallocated", slot.Id);
                    }
                }

                disconnected++;
            }

            Log.Information("Disconnected {Count}/{Total} slot(s)", disconnected, targets.Count);
            return ExitCodes.Success;
        });

        return command;
    }
}
