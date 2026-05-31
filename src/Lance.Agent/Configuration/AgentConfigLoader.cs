using System.Text.Json;

namespace Lance.Agent.Configuration;

internal static class AgentConfigLoader
{
    public const string FileName = "lance-agent.json";

    public static AgentConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            return new AgentConfig();
        }

        string json = File.ReadAllText(path);
        AgentConfig? raw = JsonSerializer.Deserialize(json, AgentConfigContext.Default.AgentConfig);
        if (raw is null)
        {
            throw new InvalidOperationException($"Config file at {path} is empty or null.");
        }

        // STJ source-generated record deserialization does not apply init-property
        // defaults for JSON sections that are absent from the file. Apply them here.
        return new AgentConfig
        {
            Listen       = raw.Listen       ?? new ListenConfig(),
            Tls          = raw.Tls          ?? new TlsConfig(),
            Auth         = raw.Auth,
            RemoteServer = raw.RemoteServer ?? new RemoteServerConfig(),
            Slots        = raw.Slots        ?? new SlotsConfig(),
            Logging      = raw.Logging      ?? new AgentLoggingConfig()
        };
    }
}
