using System.Text.Json.Serialization;

namespace Lance.Agent.Configuration;

public sealed record AgentConfig
{
    public ListenConfig Listen { get; init; } = new();
    public TlsConfig Tls { get; init; } = new();
    public AuthConfig? Auth { get; init; }
    public RemoteServerConfig RemoteServer { get; init; } = new();
    public SlotsConfig Slots { get; init; } = new();
    public AgentLoggingConfig Logging { get; init; } = new();
}

public sealed record ListenConfig
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 9876;
}

public sealed record RemoteServerConfig
{
    public string InstallDir { get; init; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo")
        : string.Empty; // [VERIFY-APOLLO] Linux path TBD

    public string ConfigDir { get; init; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo", "config")
        : string.Empty; // [VERIFY-APOLLO] Linux path TBD

    public string Executable { get; init; } = "sunshine.exe";
    public string TemplateConfigName { get; init; } = "sunshine.conf";
    public int StartupTimeoutSeconds { get; init; } = 30;
}

public sealed record SlotsConfig
{
    public int MaxCount { get; init; } = 8;
    public int PortStep { get; init; } = 1000;
    public int StopTimeoutSeconds { get; init; } = 10;
    public string NamePrefix { get; init; } = "Lance";
    public string TemplateName { get; init; } = "Lance-Template";
    public string ConfigNamePattern { get; init; } = "sunshine_{id}.conf";
}

public sealed record TlsConfig
{
    public string CertPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "lance-agent.pfx");
}

public sealed record AuthConfig
{
    public required string Token { get; init; }
}

public sealed record AgentLoggingConfig
{
    public string Level { get; init; } = "Information";
    public string FilePath { get; init; } = "lance-agent.log";
    public int RetainDays { get; init; } = 7;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentConfig))]
internal sealed partial class AgentConfigContext : JsonSerializerContext { }
