using System.CommandLine;
using Lance.Client.Configuration;
using Lance.Client.Infrastructure;
using Lance.Hooks;
using Serilog;

namespace Lance.Client.Commands;

internal static class ConnectCommand
{
    public static Command Build(GlobalOptions globals)
    {
        Option<string?> monitorsOption = new("--monitors")
        {
            Description = "Comma-separated 1-indexed monitor IDs to connect (default: all physical monitors)"
        };
        Option<string?> optionsOption = new("--options")
        {
            Description = "Extra Moonlight flags appended to each launch (e.g. \"--bitrate 80000 --fps 120\")"
        };
        Option<string?> sessionIdOption = new("--session-id")
        {
            Description = "Session id (default: a fresh random id). Must be 1-64 chars of letters, digits, '-' or '_'."
        };
        Option<string[]> hookOption = new("--hook")
        {
            Description = "Path to a hook file to run on session events (repeatable; added on top of config hooks)"
        };
        Option<string[]> monitorOptionsOption = new("--monitor-options")
        {
            Description = "Extra Moonlight options for one monitor as \"<id>=<options>\" (repeatable, e.g. \"1=--bitrate 10000\")"
        };
        Option<string?> bitrateModeOption = new("--bitrate-mode")
        {
            Description = "How each stream's bitrate is sized: high | balanced | conservative | manual | a bits-per-pixel number (default: balanced)"
        };

        Command command = new("connect", "Start a session: launch one Moonlight per monitor and block until it ends");
        command.Add(monitorsOption);
        command.Add(optionsOption);
        command.Add(sessionIdOption);
        command.Add(hookOption);
        command.Add(monitorOptionsOption);
        command.Add(bitrateModeOption);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            ClientConfig? config = globals.GetConfig();
            if (config is null)
            {
                Log.Error("Config could not be loaded — provide --config <path> or place lance.json beside the executable");
                return ExitCodes.ConfigResolutionFailed;
            }

            string? agentUrl = CommandHelpers.ResolveAgentUrl(pr, globals, config);
            if (agentUrl is null)
            {
                Log.Error("Agent URL could not be resolved — provide --agent <url> or set agent.url in lance.json");
                return ExitCodes.ConfigResolutionFailed;
            }

            Log.Debug("Targeting agent at {AgentUrl}", agentUrl);

            IReadOnlyList<MonitorInfo> allMonitors = MonitorEnumerator.Enumerate();

            IReadOnlyList<TargetMonitor>? targetMonitors = ResolveTargetMonitors(pr.GetValue(monitorsOption), allMonitors);
            if (targetMonitors is null)
            {
                return ExitCodes.Generic;
            }

            MonitorOptionsResult configMonitorOptions = MoonlightOptions.ParseConfigEntries(config.RemoteClient.MonitorOptions, allMonitors);
            if (!configMonitorOptions.IsSuccess)
            {
                Log.Error("Invalid remoteClient.monitorOptions in the config — {Reason}", configMonitorOptions.ErrorMessage);
                return ExitCodes.ConfigResolutionFailed;
            }

            MonitorOptionsResult cliMonitorOptions = MoonlightOptions.ParseCliEntries(pr.GetValue(monitorOptionsOption) ?? [], allMonitors);
            if (!cliMonitorOptions.IsSuccess)
            {
                Log.Error("Invalid --monitor-options — {Reason}", cliMonitorOptions.ErrorMessage);
                return ExitCodes.Generic;
            }

            WarnUnusedMonitorOptions(targetMonitors, configMonitorOptions.ByMonitorId, "remoteClient.monitorOptions");
            WarnUnusedMonitorOptions(targetMonitors, cliMonitorOptions.ByMonitorId, "--monitor-options");

            BitrateModeResult bitrateMode = ResolveBitrateMode(pr.GetValue(bitrateModeOption), config.RemoteClient.BitrateMode);
            if (!bitrateMode.IsSuccess)
            {
                Log.Error("Invalid bitrate mode — {Reason}", bitrateMode.ErrorMessage);
                return ExitCodes.Generic;
            }

            string sessionId = pr.GetValue(sessionIdOption) ?? Guid.NewGuid().ToString("N");
            string? token = CommandHelpers.ResolveToken(pr, globals, config);

            string? optionsStr = pr.GetValue(optionsOption);
            string[] optionTokens = optionsStr is null
                ? []
                : optionsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            IReadOnlyList<HookFileRef> hookReferences = BuildHookReferences(config, globals.GetConfigDirectory(), pr.GetValue(hookOption) ?? []);

            Log.Information("Connecting {Count} monitor(s) as session {SessionId}", targetMonitors.Count, sessionId);
            return await SessionDaemon.RunAsync(
                config, agentUrl, token, sessionId, targetMonitors, optionTokens,
                configMonitorOptions.ByMonitorId, cliMonitorOptions.ByMonitorId,
                bitrateMode.Selection, hookReferences, ct);
        });

        return command;
    }

    // The command line beats the config file. Each source also carries the option layer
    // it sits at, which decides whether an explicit --bitrate outranks the mode
    // (STREAM_TUNING_SPEC §4.2): the flag is a global CLI setting (layer 3), the config
    // field a global config setting (layer 1), and an unset mode the implicit default
    // (layer 0, so any explicit --bitrate wins).
    private static BitrateModeResult ResolveBitrateMode(string? cliMode, string? configMode)
    {
        if (!string.IsNullOrWhiteSpace(cliMode))
        {
            return BitrateModes.Parse(cliMode, sourceLayer: 3);
        }

        if (!string.IsNullOrWhiteSpace(configMode))
        {
            return BitrateModes.Parse(configMode, sourceLayer: 1);
        }

        return BitrateModes.Parse(null, sourceLayer: 0);
    }

    // Options aimed at a monitor that is not part of this connect do nothing. That is
    // almost always a typo or a stale config, so say so rather than staying silent.
    private static void WarnUnusedMonitorOptions(
        IReadOnlyList<TargetMonitor> targetMonitors, IReadOnlyDictionary<int, string[]> monitorOptions, string source)
    {
        if (monitorOptions.Count == 0)
        {
            return;
        }

        HashSet<int> connectedIds = [];
        foreach (TargetMonitor target in targetMonitors)
        {
            connectedIds.Add(target.Id);
        }

        foreach (int monitorId in monitorOptions.Keys)
        {
            if (!connectedIds.Contains(monitorId))
            {
                Log.Warning("{Source} targets monitor {Id}, which is not part of this connect — those options are ignored", source, monitorId);
            }
        }
    }

    // Returns the ordered per-position target list (Monitor is null when the requested
    // id is not present locally), or null if the input is invalid (empty selection /
    // duplicate id).
    private static IReadOnlyList<TargetMonitor>? ResolveTargetMonitors(string? monitorsStr, IReadOnlyList<MonitorInfo> allMonitors)
    {
        Dictionary<int, MonitorInfo> monitorById = [];
        foreach (MonitorInfo monitor in allMonitors)
        {
            monitorById[monitor.Id] = monitor;
        }

        List<int> targetIds = [];
        if (monitorsStr is null)
        {
            if (allMonitors.Count == 0)
            {
                Log.Error("Monitor detection failed. Use --monitors <list> to connect manually.");
                return null;
            }

            foreach (MonitorInfo monitor in allMonitors)
            {
                targetIds.Add(monitor.Id);
            }
        }
        else if (!ParseMonitorList(monitorsStr, monitorById, allMonitors.Count > 0, targetIds))
        {
            return null;
        }

        List<TargetMonitor> targets = [];
        foreach (int id in targetIds)
        {
            targets.Add(new TargetMonitor { Id = id, Monitor = monitorById.GetValueOrDefault(id) });
        }

        return targets;
    }

    private static bool ParseMonitorList(string monitorsStr, Dictionary<int, MonitorInfo> monitorById, bool canValidate, List<int> targetIds)
    {
        HashSet<int> seen = [];
        foreach (string part in monitorsStr.Split(','))
        {
            string trimmed = part.Trim();
            if (!int.TryParse(trimmed, out int id))
            {
                Log.Warning("Skipping invalid monitor ID '{Id}'", trimmed);
                continue;
            }
            if (!seen.Add(id))
            {
                Log.Error("Duplicate monitor ID {Id} in --monitors list", id);
                return false;
            }
            if (canValidate && !monitorById.ContainsKey(id))
            {
                Log.Warning("Monitor {Id} not found on this machine — skipping", id);
                continue;
            }
            targetIds.Add(id);
        }

        if (targetIds.Count == 0)
        {
            Log.Error("No valid monitor IDs in --monitors list");
            return false;
        }

        return true;
    }

    private static IReadOnlyList<HookFileRef> BuildHookReferences(ClientConfig config, string? configDirectory, string[] hookPaths)
    {
        List<HookFileRef> references = [];
        if (config.Hooks is not null)
        {
            foreach (ClientHookEntry entry in config.Hooks)
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    // Config hook paths resolve against the config file's directory.
                    references.Add(new HookFileRef { Path = entry.Path, Active = entry.Active ?? true, BaseDirectory = configDirectory });
                }
            }
        }

        // --hook flags add on top of the config list; a CLI path stays relative to the
        // current directory (no BaseDirectory), matching normal shell expectations.
        foreach (string path in hookPaths)
        {
            references.Add(new HookFileRef { Path = path, Active = true });
        }

        return references;
    }
}
