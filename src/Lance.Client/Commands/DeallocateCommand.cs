using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class DeallocateCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Argument<string> slotIdsArg = new("ids") { Description = "Slot IDs to deallocate (comma-separated, e.g. 1,2,3)" };
        Option<bool> forceOption = new("--force", "-f")
        {
            Description = "Stop each slot if running, then deallocate (skips the running check)"
        };

        Command command = new("deallocate", "Remove one or more slots' config files (refuses if running unless --force is given)");
        command.Add(slotIdsArg);
        command.Add(forceOption);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int[]? slotIds = CommandHelpers.ParseSlotIds(pr.GetValue(slotIdsArg)!, out string? parseError);
            if (slotIds is null)
            {
                Log.Error("{Error}", parseError);
                return ExitCodes.Generic;
            }

            bool force = pr.GetValue(forceOption);
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

            using AgentClient client = new(agentUrl, timeout, token);
            bool anyFailed = false;

            foreach (int slotId in slotIds)
            {
                AgentResult<bool> result = force
                    ? await client.ForceDeallocateSlotAsync(slotId, ct)
                    : await client.DeallocateSlotAsync(slotId, ct);

                // An unreachable agent will not recover within this command — stop now
                // rather than incur a timeout for every remaining slot.
                if (result.IsUnreachable)
                {
                    Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                    return result.ExitCode;
                }

                if (!result.IsSuccess)
                {
                    Log.Error("Slot {Id} deallocation failed — {ErrorCode}: {ErrorMessage}", slotId, result.ErrorCode, result.ErrorMessage);
                    anyFailed = true;
                    continue;
                }

                Log.Information("Slot {Id} deallocated", slotId);
            }

            return anyFailed ? ExitCodes.Generic : ExitCodes.Success;
        });

        return command;
    }
}
