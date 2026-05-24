using System.CommandLine;
using System.Diagnostics;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Shared.Dtos;
using Serilog;

namespace Lance.Client.Commands;

internal static class ConfigCommand
{
    public static Command Build(
        Option<string?> agentOption,
        Option<bool> noColorOption,
        Func<ClientConfig?> getConfig)
    {
        Argument<int> slotIdArg = new("slot_id") { Description = "ID of the slot whose config page to open" };

        Command command = new("config", "Open the Apollo config page for a slot in the default browser");
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
            AgentResult<ConfigUrlResponse> result = await client.GetSlotConfigUrlAsync(slotId, ct);

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

            string configUrl = result.Value!.Url;
            if (!TryOpenUrl(configUrl))
            {
                Console.WriteLine(configUrl);
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private static bool TryOpenUrl(string url)
    {
        try
        {
            ProcessStartInfo psi;

            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
                psi.ArgumentList.Add(url);
            }
            else
            {
                psi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
                psi.ArgumentList.Add(url);
            }

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
