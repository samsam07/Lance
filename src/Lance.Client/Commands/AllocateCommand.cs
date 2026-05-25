using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class AllocateCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Option<bool> noColorOption,
        Func<ClientConfig?> getConfig)
    {
        Argument<int> countArg = new("count") { Description = "Target number of slots in the pool (includes slot 0)" };

        Command command = new("allocate", "Ensure the slot pool has exactly <count> slots");
        command.Add(countArg);
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            int count = pr.GetValue(countArg);
            ClientConfig? config = getConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, agentOption, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            int timeout = config?.Agent?.TimeoutSeconds ?? 30;
            bool noColor = pr.GetValue(noColorOption);

            using AgentClient client = new(agentUrl, timeout);
            AgentResult<SlotsResponse> result = await client.AllocateSlotsAsync(count, ct);

            if (result.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return result.ExitCode;
            }

            if (!result.IsSuccess)
            {
                Log.Error("Allocation failed — {ErrorCode}: {ErrorMessage}", result.ErrorCode, result.ErrorMessage);
                return result.ExitCode;
            }

            SlotTableRenderer.Render(CommandHelpers.MakeConsole(noColor), result.Value!.Slots);
            return ExitCodes.Success;
        });

        return command;
    }
}
