using System.CommandLine;
using Lance.Client.Configuration;
using Spectre.Console;

namespace Lance.Client.Commands;

internal static class CommandHelpers
{
    public static string? ResolveAgentUrl(ParseResult pr, GlobalOptions globals, ClientConfig? config)
    {
        string? fromFlag = pr.GetValue(globals.AgentOption);
        if (fromFlag is not null) return fromFlag;
        return config?.Agent?.Url;
    }

    public static string? ResolveToken(ParseResult pr, GlobalOptions globals, ClientConfig? config)
    {
        string? fromFlag = pr.GetValue(globals.TokenOption);
        if (fromFlag is not null) return fromFlag;
        return config?.Agent?.Token;
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
