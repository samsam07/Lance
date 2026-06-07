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

            AgentResult<HealthResponse> healthResult = await client.GetHealthAsync(ct);
            if (healthResult.IsSuccess)
                CommandHelpers.CheckAgentVersion(healthResult.Value!);

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

            // Cross-reference Moonlight processes by host:port and window title
            IReadOnlyList<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
                ProcessCommandLine.FindMoonlightProcesses(executableName);

            Dictionary<int, IReadOnlyList<int>> slotToMoonlightPids = new();
            foreach (SlotDto slot in result.Value!.Slots)
            {
                IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(
                    moonlights, $"{slot.Host}:{slot.Port}", slot.Name);
                if (pids.Count > 0)
                    slotToMoonlightPids[slot.Id] = pids;
            }

            IAnsiConsole console = CommandHelpers.MakeConsole(noColor);
            RenderStatusTable(console, result.Value!.Slots, slotToMoonlightPids);
            return ExitCodes.Success;
        });

        return command;
    }

    private static void RenderStatusTable(
        IAnsiConsole console, SlotDto[] slots, Dictionary<int, IReadOnlyList<int>> slotToMoonlightPids)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("ID").RightAligned())
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn(new TableColumn("Port").RightAligned())
            .AddColumn(new TableColumn("Apollo PID").RightAligned())
            .AddColumn(new TableColumn("Moonlight PIDs").RightAligned())
            .AddColumn("Config");

        foreach (SlotDto slot in slots)
        {
            string statusCell;
            if (slot.Status == "Connected") statusCell = "[cyan]Connected[/]";
            else if (slot.Status == "Running") statusCell = "[green]Running[/]";
            else statusCell = "[yellow]Allocated[/]";

            string apolloPid = slot.ProcessId?.ToString() ?? "—";
            string moonlightPid = slotToMoonlightPids.TryGetValue(slot.Id, out IReadOnlyList<int>? pids)
                ? string.Join(", ", pids)
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
