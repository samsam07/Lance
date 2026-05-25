using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class DeallocateCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Func<ClientConfig?> getConfig)
    {
        Argument<int> slotIdArg = new("id") { Description = "Slot ID to deallocate" };

        Command command = new("deallocate", "Remove a slot's config files (refuses if running — stop first or use force-deallocate)");
        command.Add(slotIdArg);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int slotId = pr.GetValue(slotIdArg);
            ClientConfig? config = getConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, agentOption, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            int timeout = config?.Agent?.TimeoutSeconds ?? 30;

            using AgentClient client = new(agentUrl, timeout);
            AgentResult<bool> result = await client.DeallocateSlotAsync(slotId, ct);

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
