using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Shared.Dtos;
using Serilog;
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

    public static void CheckAgentVersion(HealthResponse health)
    {
        Version? clientVersion = typeof(CommandHelpers).Assembly.GetName().Version;
        if (clientVersion is null) return;

        string[] parts = health.Version.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out int agentMajor)) return;

        int clientMajor = clientVersion.Major;
        if (agentMajor < clientMajor)
            Log.Warning("Agent version {AgentVersion} is outdated (client is {ClientVersion}) — upgrade the agent",
                health.Version, FormatVersion(clientVersion));
        else if (agentMajor > clientMajor)
            Log.Warning("Client version {ClientVersion} is outdated (agent is {AgentVersion}) — upgrade the client",
                FormatVersion(clientVersion), health.Version);
    }

    private static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
