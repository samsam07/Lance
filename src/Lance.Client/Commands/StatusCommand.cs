using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class StatusCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Command command = new("status", "Show slot and session status (Phase 1: same as slots)");
        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            ClientConfig? config = globals.GetConfig();
            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, globals, config);

            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Information("Targeting agent at {AgentUrl}", agentUrl);

            bool noColor = pr.GetValue(globals.NoColorOption);
            int timeout = config?.Agent?.TimeoutSeconds ?? 30;
            string? token = CommandHelpers.ResolveToken(pr, globals, config);

            using AgentClient client = new(agentUrl, timeout, token);
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
