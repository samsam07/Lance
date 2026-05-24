namespace Lance.Agent.Configuration;

internal static class AgentConfigValidator
{
    public static void Validate(AgentConfig config)
    {
        string apolloExePath = Path.Combine(config.RemoteServer.InstallDir, config.RemoteServer.Executable);
        if (!File.Exists(apolloExePath))
        {
            throw new InvalidOperationException($"Apollo executable not found: {apolloExePath}");
        }

        if (!Directory.Exists(config.RemoteServer.ConfigDir))
        {
            throw new InvalidOperationException($"Apollo config directory not found: {config.RemoteServer.ConfigDir}");
        }

        string templatePath = Path.Combine(config.RemoteServer.ConfigDir, config.RemoteServer.TemplateConfigName);
        if (!File.Exists(templatePath))
        {
            throw new InvalidOperationException($"Apollo template config not found: {templatePath}");
        }
    }
}
