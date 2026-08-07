using System.CommandLine;
using Lance.Client.Infrastructure;
using Serilog;
using Spectre.Console;

namespace Lance.Client.Commands;

internal static class MonitorsCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Command command = new("monitors", "List physical monitors on this machine");
        command.SetAction((ParseResult pr, CancellationToken _) =>
        {
            IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.Enumerate();

            if (monitors.Count == 0)
            {
                Log.Warning("No monitors detected. Display enumeration is not supported yet on this platform.");
                return Task.FromResult(ExitCodes.Generic);
            }

            bool noColor = pr.GetValue(globals.NoColorOption);
            IAnsiConsole console = CommandHelpers.MakeConsole(noColor);

            Table table = new Table()
                .Border(TableBorder.Minimal)
                .AddColumn(new TableColumn("ID").RightAligned())
                .AddColumn(new TableColumn("Monitor").NoWrap())
                .AddColumn(new TableColumn("Device").NoWrap())
                .AddColumn(new TableColumn("Resolution").RightAligned().NoWrap())
                .AddColumn(new TableColumn("Refresh").RightAligned().NoWrap())
                .AddColumn(new TableColumn("Position").NoWrap())
                .AddColumn("Primary");

            foreach (MonitorInfo m in monitors)
            {
                string friendly = m.HasFriendlyName ? m.FriendlyName : "—";
                string resolution = $"{m.Width}×{m.Height}";
                string refresh = m.HasRefreshRate ? $"{m.RefreshRate} Hz" : "—";
                string position = $"{m.X},{m.Y}";
                string primary = m.IsPrimary ? "[green]✓[/]" : string.Empty;

                table.AddRow(
                    m.Id.ToString(),
                    Markup.Escape(friendly),
                    Markup.Escape(m.Name),
                    resolution,
                    refresh,
                    position,
                    primary);
            }

            console.Write(table);
            console.WriteLine();
            console.MarkupLine("[grey]Use the ID or the Monitor name to key --monitor-options and remoteClient.monitorOptions.[/]");
            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }
}
