using System.Text.Json.Serialization;

namespace Lance.Client.Configuration;

public sealed record ClientConfig
{
    public AgentConnectionConfig? Agent { get; init; }
    public RemoteClientConfig RemoteClient { get; init; } = new();
    public UiConfig Ui { get; init; } = new();
    public ClientHookEntry[]? Hooks { get; init; }
    public ClientLoggingConfig Logging { get; init; } = new();
}

public sealed record ClientHookEntry
{
    public string? Path { get; init; }
    public bool? Active { get; init; }   // nullable so an omitted "active" defaults to true
}

public sealed record AgentConnectionConfig
{
    public string? Url { get; init; }
    public string? Token { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record RemoteClientConfig
{
    public string Executable { get; init; } = OperatingSystem.IsWindows() ? "moonlight.exe" : "moonlight";

    // Deliberately minimal. --bitrate and --fps are omitted so each stream can be
    // sized from the monitor it targets rather than from one value shared by every
    // monitor. --yuv444 is omitted because 4:4:4 forces HEVC Range Extensions, which
    // many GPUs cannot decode on their fast path — it silently drops the client onto
    // a slower fallback decoder.
    public string[] DefaultOptions { get; init; } = [
        "--video-codec", "HEVC",
        "--capture-system-keys", "fullscreen"
    ];

    // Extra options for one monitor, keyed by the id `lance monitors` reports. Applied
    // after DefaultOptions, so a per-monitor value wins. Referring to a monitor by name
    // is designed but not built yet — see docs/STREAM_TUNING_SPEC.md §8.
    public Dictionary<string, string[]>? MonitorOptions { get; init; }

    // How each stream's bitrate is chosen: "high" | "balanced" | "conservative" |
    // "manual" | a bits-per-pixel number. Unset means "balanced". Overridden by
    // --bitrate-mode. See docs/STREAM_TUNING_SPEC.md §4.
    public string? BitrateMode { get; init; }
}

public sealed record UiConfig
{
    public bool Color { get; init; } = true;
}

public sealed record ClientLoggingConfig
{
    public string Level { get; init; } = "Information";
    public string? FilePath { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClientConfig))]
internal sealed partial class ClientConfigContext : JsonSerializerContext { }
