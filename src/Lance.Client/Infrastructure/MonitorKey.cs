using Serilog;

namespace Lance.Client.Infrastructure;

// The outcome of turning one user-written monitor key into a monitor. MonitorId is
// null when the key named no monitor at all — a warning rather than a failure, so a
// stale entry never blocks a connect.
internal readonly record struct MonitorKeyResolution(int? MonitorId, string? ErrorMessage);

// How every user-written reference to a monitor is resolved — `--monitors`,
// `monitorOptions` and `--monitor-options` all come through here so the rules cannot
// drift apart between them. A key is either an id ("3") or a name ("U28E590").
internal static class MonitorKey
{
    public static MonitorKeyResolution Resolve(string rawKey, IReadOnlyList<MonitorInfo> monitors, string source)
    {
        // Trimmed here rather than at each call site so every route in gets the same
        // treatment — a JSON key carries whatever whitespace the file has, and
        // int.TryParse would tolerate it for an id while a name silently missed.
        string key = rawKey.Trim();

        // An id is taken at face value. Whether that monitor exists is the caller's
        // business: `--monitors` still needs to accept ids when display detection
        // failed entirely.
        if (int.TryParse(key, out int monitorId))
        {
            return new MonitorKeyResolution(monitorId, null);
        }

        List<MonitorInfo> matches = [];
        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.MatchesName(key))
            {
                matches.Add(monitor);
            }
        }

        if (matches.Count == 0)
        {
            Log.Warning("{Source}: no monitor is called '{Key}' — it is ignored. Run `lance monitors` to see the names.", source, key);
            return new MonitorKeyResolution(null, null);
        }

        if (matches.Count > 1)
        {
            // Two panels of the same model share a name, so the intent is genuinely
            // ambiguous. Ids are the unambiguous way to say which one.
            List<int> ids = [];
            foreach (MonitorInfo monitor in matches)
            {
                ids.Add(monitor.Id);
            }

            return new MonitorKeyResolution(
                null,
                $"{source}: '{key}' matches more than one monitor ({string.Join(", ", ids)}). Use the monitor id instead.");
        }

        return new MonitorKeyResolution(matches[0].Id, null);
    }

    // How a monitor is named back to the user. Someone who typed a name should not have
    // to translate an id in the reply, so the name is carried whenever it is known.
    public static string Describe(MonitorInfo? monitor, int monitorId)
    {
        if (monitor is MonitorInfo target && target.HasFriendlyName)
        {
            return $"{monitorId} ({target.FriendlyName})";
        }

        return monitorId.ToString();
    }

    public static string Describe(IReadOnlyList<MonitorInfo> monitors, int monitorId)
    {
        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.Id == monitorId)
            {
                return Describe(monitor, monitorId);
            }
        }

        return monitorId.ToString();
    }
}
