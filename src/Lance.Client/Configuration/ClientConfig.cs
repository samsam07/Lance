using System.Text.Json.Serialization;

namespace Lance.Client.Configuration;

public sealed record ClientConfig
{
    public AgentConnectionConfig? Agent { get; init; }
    public RemoteClientConfig RemoteClient { get; init; } = new();
    public UiConfig Ui { get; init; } = new();
    public ClientLoggingConfig Logging { get; init; } = new();
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
    public string[] DefaultFlags { get; init; } = [
        "--fps", "60",
        "--video-codec", "HEVC",
        "--bitrate", "80000",
        "--no-vsync"
    ];
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
