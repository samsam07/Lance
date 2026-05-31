using System.Net;
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

            if (!string.IsNullOrEmpty(config.Auth?.Token))
                Log.Information("Bearer token authentication is enabled");
            else
                Log.Warning("No auth token configured — agent API is open to all callers");

            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                IPAddress listenAddress = config.Listen.Host switch
                {
                    "0.0.0.0" or "*" => IPAddress.Any,
                    "::" => IPAddress.IPv6Any,
                    _ => IPAddress.TryParse(config.Listen.Host, out IPAddress? ip) ? ip : IPAddress.Loopback
                };

                serverOptions.Listen(listenAddress, config.Listen.Port, listenOptions =>
                {
                    listenOptions.UseHttps();
                });
            });

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
            builder.Services.AddSingleton<ITcpProbe, TcpProbe>();
            builder.Services.AddSingleton<ISlotScanner, SlotScanner>();
            builder.Services.AddSingleton<ISlotAllocator, SlotAllocator>();
            builder.Services.AddSingleton<IProcessTracker, ProcessTracker>();
            builder.Services.AddSingleton<ISlotLifecycle, SlotLifecycle>();
            builder.Services.AddTransient<BearerTokenMiddleware>();
            builder.Services.AddTransient<HttpBodyLoggingMiddleware>();

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

            app.UseMiddleware<BearerTokenMiddleware>();

            if (level <= LogEventLevel.Debug)
            {
                app.UseMiddleware<HttpBodyLoggingMiddleware>();
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
}
