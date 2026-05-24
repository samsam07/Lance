using Lance.Agent.Configuration;
using Lance.Shared.Dtos;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lance.Agent.Endpoints;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, DateTimeOffset startedAt)
    {
        app.MapGet("/health", (AgentConfig config) => GetHealth(config, startedAt));
    }

    private static Ok<HealthResponse> GetHealth(AgentConfig config, DateTimeOffset startedAt)
    {
        string templatePath = Path.Combine(config.RemoteServer.ConfigDir, config.RemoteServer.TemplateConfigName);

        return TypedResults.Ok(new HealthResponse
        {
            Status = "ok",
            Version = GetVersion(),
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            MaxSlots = config.Slots.MaxCount,
            TemplatePath = templatePath,
            TemplateExists = File.Exists(templatePath)
        });
    }

    private static string GetVersion()
    {
        Version? version = typeof(HealthEndpoints).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
