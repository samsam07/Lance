using System.Globalization;
using Serilog;

namespace Lance.Client.Infrastructure;

// A per-monitor option map (config `monitorOptions`, or repeated --monitor-options)
// resolved down to monitor ids. See docs/design/stream-tuning.md §3 and §8.
internal sealed record MonitorOptionsResult
{
    public IReadOnlyDictionary<int, string[]> ByMonitorId { get; init; } = new Dictionary<int, string[]>();
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorMessage is null;
}

// The four author-supplied option layers for one stream, lowest precedence first.
// Layer numbers here are the ones §4.2 compares against BitrateSelection.SourceLayer.
internal sealed record OptionLayers
{
    public string[] DefaultOptions { get; init; } = [];        // layer 1
    public string[] MonitorOptions { get; init; } = [];        // layer 2
    public string[] CliOptions { get; init; } = [];            // layer 3
    public string[] CliMonitorOptions { get; init; } = [];     // layer 4
}

// Builds the Moonlight argument list for one stream by appending option layers,
// lowest precedence first (docs/design/stream-tuning.md §3). Moonlight resolves a repeated
// option by taking its last occurrence, so Lance only has to emit the layers in
// order — it never has to dedupe or rewrite what the user wrote.
internal static class MoonlightOptions
{
    // Ceiling for the generated --fps. A 144 Hz panel showing a desktop gains little
    // from the extra frames but costs the agent's encoder and uplink a great deal, so
    // the generated default stays at 60. Asking for more is done explicitly, by
    // putting --fps in any later layer.
    public const int MaxGeneratedFps = 60;

    // The same ceiling applied to the bitrate arithmetic. It bites only when the user
    // has raised --fps themselves, and stops an automatic mode from producing a
    // 4K144-scale number nobody asked for.
    public const int MaxDerivedFps = 60;

    private const string BitrateOption = "--bitrate";
    private const string FpsOption = "--fps";
    private const string ResolutionOption = "--resolution";

    public static MonitorOptionsResult ParseConfigEntries(
        IReadOnlyDictionary<string, string[]>? entries, IReadOnlyList<MonitorInfo> monitors)
    {
        if (entries is null || entries.Count == 0)
        {
            return new MonitorOptionsResult();
        }

        Dictionary<int, string[]> byMonitorId = [];
        foreach ((string key, string[] tokens) in entries)
        {
            MonitorKeyResolution resolved = MonitorKey.Resolve(key, monitors, "monitorOptions");
            if (resolved.ErrorMessage is not null)
            {
                return new MonitorOptionsResult { ErrorMessage = resolved.ErrorMessage };
            }

            if (resolved.MonitorId is not int monitorId)
            {
                continue;
            }

            if (byMonitorId.ContainsKey(monitorId))
            {
                return new MonitorOptionsResult
                {
                    ErrorMessage = $"monitorOptions refers to monitor {MonitorKey.Describe(monitors, monitorId)} more than once."
                };
            }

            byMonitorId[monitorId] = tokens;
        }

        return new MonitorOptionsResult { ByMonitorId = byMonitorId };
    }

    public static MonitorOptionsResult ParseCliEntries(string[] entries, IReadOnlyList<MonitorInfo> monitors)
    {
        Dictionary<int, List<string>> byMonitorId = [];
        foreach (string entry in entries)
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                return new MonitorOptionsResult
                {
                    ErrorMessage = $"--monitor-options '{entry}' is malformed. Expected \"<monitor>=<options>\", for example \"1=--bitrate 10000\"."
                };
            }

            MonitorKeyResolution resolvedKey = MonitorKey.Resolve(entry[..separator].Trim(), monitors, "--monitor-options");
            if (resolvedKey.ErrorMessage is not null)
            {
                return new MonitorOptionsResult { ErrorMessage = resolvedKey.ErrorMessage };
            }

            if (resolvedKey.MonitorId is not int monitorId)
            {
                continue;
            }

            string[] tokens = entry[(separator + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (!byMonitorId.TryGetValue(monitorId, out List<string>? collected))
            {
                collected = [];
                byMonitorId[monitorId] = collected;
            }

            // Repeating --monitor-options for the same monitor appends rather than
            // replaces, so a set of options can be built up across several arguments.
            collected.AddRange(tokens);
        }

        Dictionary<int, string[]> resolved = new(byMonitorId.Count);
        foreach ((int monitorId, List<string> tokens) in byMonitorId)
        {
            resolved[monitorId] = [.. tokens];
        }

        return new MonitorOptionsResult { ByMonitorId = resolved };
    }

    public static string[] Build(MonitorInfo? monitor, int monitorId, OptionLayers layers, BitrateSelection bitrate)
    {
        List<string> options = [];

        // Layer 0 — generated from the target monitor. Emitted first precisely so that
        // any configured or command-line layer can override it (e.g. streaming a 4K
        // panel at 1440p to cut encoder load and bandwidth).
        if (monitor is MonitorInfo target)
        {
            options.Add(ResolutionOption);
            options.Add($"{target.Width}x{target.Height}");

            // Only a rate we actually know is worth sending; otherwise leave the frame
            // rate to Moonlight, exactly as before.
            if (target.HasRefreshRate)
            {
                options.Add(FpsOption);
                options.Add(Math.Min(target.RefreshRate, MaxGeneratedFps).ToString(CultureInfo.InvariantCulture));
            }
        }

        options.AddRange(layers.DefaultOptions);
        options.AddRange(layers.MonitorOptions);
        options.AddRange(layers.CliOptions);
        options.AddRange(layers.CliMonitorOptions);

        AppendDerivedBitrate(options, MonitorKey.Describe(monitor, monitorId), layers, bitrate);

        return [.. options];
    }

    private static void AppendDerivedBitrate(List<string> options, string monitor, OptionLayers layers, BitrateSelection bitrate)
    {
        if (bitrate.IsManual)
        {
            return;
        }

        int explicitLayer = HighestLayerWith(layers, BitrateOption);
        if (explicitLayer >= bitrate.SourceLayer)
        {
            // An explicit bitrate at the mode's own level or above outranks it. Only
            // worth mentioning when a mode was actually asked for; with no mode set
            // this is just the ordinary "I chose my own bitrate" case.
            if (bitrate.SourceLayer > 0)
            {
                Log.Warning(
                    "Monitor {Monitor}: an explicit --bitrate overrides the {Mode} bitrate mode for this stream",
                    monitor, bitrate.Name);
            }

            return;
        }

        if (!TryReadResolution(options, out int width, out int height))
        {
            Log.Debug("Monitor {Monitor}: no resolution is known, so the bitrate is left to Moonlight", monitor);
            return;
        }

        int fps = ReadFps(options);
        int fpsForMath = Math.Min(fps, MaxDerivedFps);
        int kbps = DeriveKbps(width, height, fpsForMath, bitrate.BitsPerPixel);

        if (fps > MaxDerivedFps)
        {
            Log.Warning(
                "Monitor {Monitor}: streaming at {Fps}fps but the automatic bitrate is capped at {Cap}fps-equivalent ({Kbps} kbps) — set --bitrate explicitly for a full-rate budget",
                monitor, fps, MaxDerivedFps, kbps);
        }

        if (explicitLayer >= 0)
        {
            Log.Warning(
                "Monitor {Monitor}: the {Mode} bitrate mode overrides the --bitrate set in the configuration ({Kbps} kbps)",
                monitor, bitrate.Name, kbps);
        }

        Log.Information(
            "Monitor {Monitor}: {Width}x{Height} at {Fps}fps — {Kbps} kbps ({Mode})",
            monitor, width, height, fps, kbps, bitrate.Name);

        options.Add(BitrateOption);
        options.Add(kbps.ToString(CultureInfo.InvariantCulture));
    }

    private static int HighestLayerWith(OptionLayers layers, string option)
    {
        int highest = -1;
        if (Array.IndexOf(layers.DefaultOptions, option) >= 0)
        {
            highest = 1;
        }

        if (Array.IndexOf(layers.MonitorOptions, option) >= 0)
        {
            highest = 2;
        }

        if (Array.IndexOf(layers.CliOptions, option) >= 0)
        {
            highest = 3;
        }

        if (Array.IndexOf(layers.CliMonitorOptions, option) >= 0)
        {
            highest = 4;
        }

        return highest;
    }

    private static bool TryReadResolution(List<string> options, out int width, out int height)
    {
        width = 0;
        height = 0;

        string? value = ReadLastValue(options, ResolutionOption);
        if (value is null)
        {
            return false;
        }

        int separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (separator <= 0)
        {
            return false;
        }

        return int.TryParse(value[..separator], out width)
            && int.TryParse(value[(separator + 1)..], out height)
            && width > 0
            && height > 0;
    }

    private static int ReadFps(List<string> options)
    {
        string? value = ReadLastValue(options, FpsOption);
        if (value is not null && int.TryParse(value, out int fps) && fps > 0)
        {
            return fps;
        }

        // Nothing said otherwise — Moonlight's own default rate.
        return MaxGeneratedFps;
    }

    // Moonlight takes the last occurrence of a repeated option, so the effective value
    // of an option is whatever the highest layer set.
    private static string? ReadLastValue(List<string> options, string option)
    {
        for (int i = options.Count - 2; i >= 0; i--)
        {
            if (string.Equals(options[i], option, StringComparison.Ordinal))
            {
                return options[i + 1];
            }
        }

        return null;
    }

    private static int DeriveKbps(int width, int height, int fps, double bitsPerPixel)
    {
        double bitsPerSecond = (double)width * height * fps * bitsPerPixel;
        double kbps = bitsPerSecond / 1000.0;

        // Round to the nearest Mbps so the figure reads as a decision, not a
        // calculation, and never fall below 1 Mbps.
        int rounded = (int)Math.Round(kbps / 1000.0, MidpointRounding.AwayFromZero) * 1000;

        return Math.Max(rounded, 1000);
    }
}
