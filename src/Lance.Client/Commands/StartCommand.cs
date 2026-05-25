using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Serilog;

namespace Lance.Client.Commands;

internal static class StartCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Func<ClientConfig?> getConfig)
    {
        Argument<int> slotIdArg = new("id") { Description = "Slot ID to start" };

        Command command = new("start", "Start the Apollo instance for a slot");
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
            AgentResult<bool> result = await client.StartSlotAsync(slotId, ct);

            if (result.IsUnreachable)
            {
                Log.Error("Agent unreachable at {AgentUrl}", agentUrl);
                return result.ExitCode;
            }

            if (!result.IsSuccess)
            {
                Log.Error("Slot {Id} failed to start — {ErrorCode}: {ErrorMessage}", slotId, result.ErrorCode, result.ErrorMessage);
                return result.ExitCode;
            }

            Log.Information("Slot {Id} started", slotId);
            return ExitCodes.Success;
        });

        return command;
    }
}
