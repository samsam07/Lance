using Serilog;

namespace Lance.Client.Infrastructure;

// One resolved entry from --monitors: the id the user asked for, and the local monitor
// it maps to. Monitor is null when that id is not present on this machine — the id is
// still carried so per-monitor options can be reported against it.
internal sealed record TargetMonitor
{
    public int Id { get; init; }
    public MonitorInfo? Monitor { get; init; }
}

internal sealed record MonitorSelectionResult
{
    public IReadOnlyList<TargetMonitor> Targets { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorMessage is null;
}

// Turns --monitors into the ordered list of monitors to connect. Order is load-bearing:
// position i maps to slot i and supplies that stream's resolution, so the list is never
// sorted or de-duplicated into a different shape.
internal static class MonitorSelection
{
    private const string Source = "--monitors";

    public static MonitorSelectionResult Resolve(string? monitorsValue, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitorsValue is null)
        {
            return SelectEveryMonitor(monitors);
        }

        Dictionary<int, MonitorInfo> monitorById = [];
        foreach (MonitorInfo monitor in monitors)
        {
            monitorById[monitor.Id] = monitor;
        }

        // Remembers which key first claimed each monitor, so a duplicate can name both
        // spellings rather than just the number they happen to share.
        Dictionary<int, string> claimedBy = [];
        List<TargetMonitor> targets = [];

        foreach (string part in monitorsValue.Split(','))
        {
            string key = part.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            MonitorKeyResolution resolved = MonitorKey.Resolve(key, monitors, Source);
            if (resolved.ErrorMessage is not null)
            {
                return new MonitorSelectionResult { ErrorMessage = resolved.ErrorMessage };
            }

            // Named nothing — MonitorKey has already said so.
            if (resolved.MonitorId is not int monitorId)
            {
                continue;
            }

            // Detection having failed entirely is not a reason to refuse: ids are still
            // accepted so the user can connect manually.
            if (monitors.Count > 0 && !monitorById.ContainsKey(monitorId))
            {
                Log.Warning("Monitor {Id} is not attached to this machine — skipping it", monitorId);
                continue;
            }

            if (claimedBy.TryGetValue(monitorId, out string? firstKey))
            {
                string spellings = string.Equals(firstKey, key, StringComparison.OrdinalIgnoreCase)
                    ? $"'{key}'"
                    : $"'{firstKey}' and '{key}'";

                return new MonitorSelectionResult
                {
                    ErrorMessage = $"{Source} refers to monitor {MonitorKey.Describe(monitors, monitorId)} more than once ({spellings})."
                };
            }

            claimedBy[monitorId] = key;
            targets.Add(new TargetMonitor { Id = monitorId, Monitor = monitorById.GetValueOrDefault(monitorId) });
        }

        if (targets.Count == 0)
        {
            return new MonitorSelectionResult
            {
                ErrorMessage = $"No usable monitors in {Source}. Run `lance monitors` to see the ids and names."
            };
        }

        return new MonitorSelectionResult { Targets = targets };
    }

    private static MonitorSelectionResult SelectEveryMonitor(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return new MonitorSelectionResult
            {
                ErrorMessage = $"Monitor detection failed. Use {Source} <list> to connect manually."
            };
        }

        List<TargetMonitor> targets = [];
        foreach (MonitorInfo monitor in monitors)
        {
            targets.Add(new TargetMonitor { Id = monitor.Id, Monitor = monitor });
        }

        return new MonitorSelectionResult { Targets = targets };
    }
}
