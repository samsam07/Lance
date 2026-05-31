using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Http;
using Lance.Client.Infrastructure;
using Lance.Shared.Dtos;
using Serilog;
using Spectre.Console;

namespace Lance.Client.Commands;

internal static class StatusCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Command command = new("status", "Show slot state and cross-referenced local Moonlight processes");
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
            string executableName = config?.RemoteClient.Executable ?? "moonlight";

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

            // Cross-reference Moonlight processes by host:port
            IReadOnlyList<(int Pid, string CommandLine)> moonlights =
                ProcessCommandLine.FindMoonlightProcesses(executableName);

            Dictionary<int, int> slotToMoonlightPid = new();
            foreach (SlotDto slot in result.Value!.Slots)
            {
                string hostPort = $"{slot.Host}:{slot.Port}";
                foreach ((int Pid, string CommandLine) m in moonlights)
                {
                    if (m.CommandLine.Contains(hostPort, StringComparison.OrdinalIgnoreCase))
                    {
                        slotToMoonlightPid[slot.Id] = m.Pid;
                        break;
                    }
                }
            }

            IAnsiConsole console = CommandHelpers.MakeConsole(noColor);
            RenderStatusTable(console, result.Value!.Slots, slotToMoonlightPid);
            return ExitCodes.Success;
        });

        return command;
    }

    private static void RenderStatusTable(
        IAnsiConsole console, SlotDto[] slots, Dictionary<int, int> slotToMoonlightPid)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("ID").RightAligned())
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn(new TableColumn("Port").RightAligned())
            .AddColumn(new TableColumn("Apollo PID").RightAligned())
            .AddColumn(new TableColumn("Moonlight PID").RightAligned())
            .AddColumn("Config");

        foreach (SlotDto slot in slots)
        {
            string statusCell;
            if (slot.Status == "Connected") statusCell = "[cyan]Connected[/]";
            else if (slot.Status == "Running") statusCell = "[green]Running[/]";
            else statusCell = "[yellow]Allocated[/]";

            string apolloPid = slot.ProcessId?.ToString() ?? "—";
            string moonlightPid = slotToMoonlightPid.TryGetValue(slot.Id, out int mpid)
                ? mpid.ToString()
                : "—";

            string nameCell = Markup.Escape(slot.Name);
            if (slot.IsTemplate) nameCell += " [dim](template)[/]";
            if (slot.IsAdopted) nameCell += " [dim](adopted)[/]";

            table.AddRow(
                slot.Id.ToString(),
                nameCell,
                statusCell,
                slot.Port.ToString(),
                apolloPid,
                moonlightPid,
                Markup.Escape(slot.ConfigName));
        }

        console.Write(table);
    }
}
