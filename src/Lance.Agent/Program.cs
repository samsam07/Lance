using System.Text;
using Lance.Agent.Configuration;
using Lance.Agent.Endpoints;
using Lance.Agent.Infrastructure;
using Lance.Agent.Services;
using Lance.Shared.Serialization;
using Serilog;
using Serilog.Events;

namespace Lance.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, AgentConfigLoader.FileName);
            AgentConfig config = AgentConfigLoader.Load();
            if (File.Exists(configPath))
                Log.Information("Config loaded from {ConfigPath}", configPath);
            else
                Log.Warning("Config file not found at {ConfigPath} — running with defaults", configPath);

            AdminGuard.RequireElevation();

            LogEventLevel level = Enum.TryParse<LogEventLevel>(config.Logging.Level, ignoreCase: true, out LogEventLevel parsed)
                ? parsed
                : LogEventLevel.Information;

            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

            // Phase 1: HTTP only. Explicitly override launchSettings.json / ASPNETCORE_URLS
            // so the agent always binds to the configured host:port via plain HTTP.
            builder.WebHost.UseUrls($"http://{config.Listen.Host}:{config.Listen.Port}");

            builder.Host.UseSerilog((_, loggerConfig) =>
            {
                loggerConfig
                    .MinimumLevel.Is(level)
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        config.Logging.FilePath,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.Logging.RetainDays);
            });

            builder.Services.ConfigureHttpJsonOptions(opts =>
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, LanceSharedJsonContext.Default));

            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<ISlotScanner, SlotScanner>();
            builder.Services.AddSingleton<ISlotAllocator, SlotAllocator>();
            builder.Services.AddSingleton<IProcessTracker, ProcessTracker>();
            builder.Services.AddSingleton<ISlotLifecycle, SlotLifecycle>();

            WebApplication app = builder.Build();

            AgentConfigValidator.Validate(config);

            IProcessTracker tracker = app.Services.GetRequiredService<IProcessTracker>();
            ISlotLifecycle lifecycle = app.Services.GetRequiredService<ISlotLifecycle>();

            Microsoft.Extensions.Logging.ILogger adoptLogger = app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ProcessAdopter).FullName!);
            ProcessAdopter.Adopt(config, tracker, adoptLogger);

            int standardAdopted = 0;
            int nonStandardAdopted = 0;
            foreach ((int slotId, _) in tracker.GetAll())
            {
                if (slotId >= 1000)
                    nonStandardAdopted++;
                else
                    standardAdopted++;
            }
            Log.Information("Adoption complete: {Standard} standard slot(s), {NonStandard} non-standard slot(s) adopted",
                standardAdopted, nonStandardAdopted);

            // Synchronous callback forced by the ASP.NET Core API — the one permitted
            // deviation from the "no GetAwaiter().GetResult()" rule in CONVENTIONS.md.
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                Log.Information("Lance agent stopping");
                IReadOnlyList<(int SlotId, SlotProcess Entry)> running = tracker.GetAll();
                Task[] tasks = new Task[running.Count];
                for (int i = 0; i < running.Count; i++)
                {
                    tasks[i] = lifecycle.StopAsync(running[i].SlotId);
                }
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            });

            if (level <= LogEventLevel.Debug)
            {
                Microsoft.Extensions.Logging.ILogger httpBodyLogger = app.Services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Lance.Agent.HttpBody");
                app.Use(async (context, next) =>
                {
                    await LogHttpBodiesAsync(context, next, httpBodyLogger);
                });
            }

            app.MapHealthEndpoints(startedAt);
            app.MapSlotEndpoints();

            await app.RunAsync();
            Log.Information("Lance agent stopped — graceful shutdown complete");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agent failed to start");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task LogHttpBodiesAsync(
        HttpContext context, RequestDelegate next, Microsoft.Extensions.Logging.ILogger logger)
    {
        context.Request.EnableBuffering();
        string requestBody = await ReadStreamAsync(context.Request.Body);
        context.Request.Body.Position = 0;
        if (requestBody.Length > 0)
        {
            logger.LogDebug("Request body: {Body}", requestBody);
        }

        Stream originalBody = context.Response.Body;
        using MemoryStream capture = new();
        context.Response.Body = capture;
        try
        {
            await next(context);
        }
        finally
        {
            capture.Position = 0;
            string responseBody = await ReadStreamAsync(capture);
            if (responseBody.Length > 0)
            {
                logger.LogDebug("Response body: {Body}", responseBody);
            }
            capture.Position = 0;
            await capture.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> ReadStreamAsync(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
