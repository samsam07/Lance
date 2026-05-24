using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class StatusCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Option<bool> noColorOption,
        Func<ClientConfig?> getConfig)
    {
        Command command = new("status", "Show slot and session status (Phase 1: same as slots)");
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            ClientConfig? config = getConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, agentOption, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            bool noColor = pr.GetValue(noColorOption);
            int timeout = config?.Agent?.TimeoutSeconds ?? 30;

            using AgentClient client = new(agentUrl, timeout);
            AgentResult<SlotsResponse> result = await client.GetSlotsAsync(ct);

            if (result.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return result.ExitCode;
            }

            if (!result.IsSuccess)
            {
                Log.Error("Agent returned error {ErrorCode}: {ErrorMessage}", result.ErrorCode, result.ErrorMessage);
                return result.ExitCode;
            }

            SlotTableRenderer.Render(
                CommandHelpers.MakeConsole(noColor),
                result.Value!.Slots);
            return ExitCodes.Success;
        });

        return command;
    }
}
