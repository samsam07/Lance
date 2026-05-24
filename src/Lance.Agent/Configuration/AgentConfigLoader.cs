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
        AgentConfig? config = JsonSerializer.Deserialize(json, AgentConfigContext.Default.AgentConfig);
        if (config is null)
        {
            throw new InvalidOperationException($"Config file at {path} is empty or null.");
        }

        return config;
    }
}
