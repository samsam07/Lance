using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class DeallocateCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Argument<int> slotIdArg = new("id") { Description = "Slot ID to deallocate" };
        Option<bool> forceOption = new("--force", "-f")
        {
            Description = "Stop the slot if running, then deallocate (skips the running check)"
        };

        Command command = new("deallocate", "Remove a slot's config files (refuses if running unless --force is given)");
        command.Add(slotIdArg);
        command.Add(forceOption);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int slotId = pr.GetValue(slotIdArg);
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
            AgentResult<bool> result = force
                ? await client.ForceDeallocateSlotAsync(slotId, ct)
                : await client.DeallocateSlotAsync(slotId, ct);

            if (result.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return result.ExitCode;
            }

            if (!result.IsSuccess)
            {
                Log.Error("Slot {Id} deallocation failed — {ErrorCode}: {ErrorMessage}", slotId, result.ErrorCode, result.ErrorMessage);
                return result.ExitCode;
            }

            Log.Information("Slot {Id} deallocated", slotId);
            return ExitCodes.Success;
        });

        return command;
    }
}
