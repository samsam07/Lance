using System.CommandLine;
using Lance.Client.Configuration;
using Spectre.Console;

namespace Lance.Client.Commands;

internal static class CommandHelpers
{
    public static string? ResolveAgentUrl(ParseResult pr, Option<string?> agentOption, ClientConfig? config)
    {
        string? fromFlag = pr.GetValue(agentOption);
        if (fromFlag is not null) return fromFlag;
        return config?.Agent?.Url;
    }

    public static IAnsiConsole MakeConsole(bool noColor)
    {
        AnsiConsoleSettings settings = new()
        {
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Detect
        };
        return AnsiConsole.Create(settings);
    }
}
