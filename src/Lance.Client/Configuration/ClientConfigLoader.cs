using System.Text.Json;

namespace Lance.Client.Configuration;

internal static class ClientConfigLoader
{
    public const string FileName = "lance.json";

    public static ClientConfig? Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return LoadFrom(path);
    }

    public static ClientConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}", path);
        }

        string json = File.ReadAllText(path);
        ClientConfig? config = JsonSerializer.Deserialize(json, ClientConfigContext.Default.ClientConfig);
        if (config is null)
        {
            throw new InvalidOperationException("Config file is empty or null.");
        }

        return config;
    }
}
