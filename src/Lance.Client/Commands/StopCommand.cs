using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class StopCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Argument<string> slotIdsArg = new("ids") { Description = "Slot IDs to stop (comma-separated, e.g. 1,2,3)" };

        Command command = new("stop", "Stop the Apollo instance for one or more slots");
        command.Add(slotIdsArg);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int[]? slotIds = CommandHelpers.ParseSlotIds(pr.GetValue(slotIdsArg)!, out string? parseError);
            if (slotIds is null)
            {
                Log.Error("{Error}", parseError);
                return ExitCodes.Generic;
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

            using AgentClient client = new(agentUrl, timeout, token);
            bool anyFailed = false;

            foreach (int slotId in slotIds)
            {
                AgentResult<bool> result = await client.StopSlotAsync(slotId, ct);

                // An unreachable agent will not recover within this command — stop now
                // rather than incur a timeout for every remaining slot.
                if (result.IsUnreachable)
                {
                    Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                    return result.ExitCode;
                }

                if (!result.IsSuccess)
                {
                    Log.Error("Slot {Id} failed to stop — {ErrorCode}: {ErrorMessage}", slotId, result.ErrorCode, result.ErrorMessage);
                    anyFailed = true;
                    continue;
                }

                Log.Information("Slot {Id} stopped", slotId);
            }

            return anyFailed ? ExitCodes.Generic : ExitCodes.Success;
        });

        return command;
    }
}
