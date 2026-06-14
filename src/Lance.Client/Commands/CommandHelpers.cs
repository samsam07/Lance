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

    // Parses a comma-separated slot-id list ("1,2,3") into distinct ids, preserving
    // order. Duplicates are dropped with a warning (intent is clear); any non-integer
    // or empty token (e.g. a stray comma) is a hard parse error. Range is not checked
    // here — the agent is the authority and returns a proper error per slot.
    public static int[]? ParseSlotIds(string raw, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "no slot ids provided";
            return null;
        }

        string[] tokens = raw.Split(',');
        List<int> ids = new(tokens.Length);
        HashSet<int> seen = new();

        foreach (string token in tokens)
        {
            string trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                error = $"invalid slot ids '{raw}' — empty id (check for a stray or trailing comma)";
                return null;
            }

            if (!int.TryParse(trimmed, out int id))
            {
                error = $"invalid slot id '{trimmed}' — expected a comma-separated list of integers (e.g. 1,2,3)";
                return null;
            }

            if (!seen.Add(id))
            {
                Log.Warning("Duplicate slot id {Id} ignored", id);
                continue;
            }

            ids.Add(id);
        }

        return ids.ToArray();
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
