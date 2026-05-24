using Lance.Shared.Dtos;
using Spectre.Console;

namespace Lance.Client.Commands;

internal static class SlotTableRenderer
{
    public static void Render(IAnsiConsole console, SlotDto[] slots)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("ID").RightAligned())
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn(new TableColumn("Port").RightAligned())
            .AddColumn(new TableColumn("PID").RightAligned())
            .AddColumn("Config");

        foreach (SlotDto slot in slots)
        {
            string statusCell = slot.Status == "Running" ? "[green]Running[/]" : "[yellow]Allocated[/]";
            string pidCell = slot.ProcessId?.ToString() ?? "—";

            string nameCell = Markup.Escape(slot.Name);
            if (slot.IsTemplate) nameCell += " [dim](template)[/]";
            if (slot.IsAdopted) nameCell += " [dim](adopted)[/]";

            table.AddRow(
                slot.Id.ToString(),
                nameCell,
                statusCell,
                slot.Port.ToString(),
                pidCell,
                Markup.Escape(slot.ConfigName));
        }

        console.Write(table);
    }
}
