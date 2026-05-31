using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class ForceDeallocateCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Argument<int> slotIdArg = new("id") { Description = "Slot ID to force-deallocate" };

        Command command = new("force-deallocate", "Stop a slot if running, then remove its config files");
        command.Add(slotIdArg);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int slotId = pr.GetValue(slotIdArg);
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
            AgentResult<bool> result = await client.ForceDeallocateSlotAsync(slotId, ct);

            if (result.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return result.ExitCode;
            }

            if (!result.IsSuccess)
            {
                Log.Error("Slot {Id} force-deallocation failed — {ErrorCode}: {ErrorMessage}", slotId, result.ErrorCode, result.ErrorMessage);
                return result.ExitCode;
            }

            Log.Information("Slot {Id} force-deallocated", slotId);
            return ExitCodes.Success;
        });

        return command;
    }
}
