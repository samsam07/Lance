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
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("ID").RightAligned())
                .AddColumn("Name")
                .AddColumn(new TableColumn("Resolution").RightAligned())
                .AddColumn("Position")
                .AddColumn("Primary");

            foreach (MonitorInfo m in monitors)
            {
                string resolution = $"{m.Width}×{m.Height}";
                string position = $"{m.X},{m.Y}";
                string primary = m.IsPrimary ? "[green]✓[/]" : string.Empty;

                table.AddRow(
                    m.Id.ToString(),
                    Markup.Escape(m.Name),
                    resolution,
                    position,
                    primary);
            }

            console.Write(table);
            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }
}
