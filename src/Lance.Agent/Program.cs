using System.Net;
using Lance.Agent.Configuration;
using Lance.Agent.Endpoints;
using Lance.Agent.Infrastructure;
using Lance.Agent.Services;
using Lance.Agent.Sessions;
using Lance.Hooks;
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

            AdminGuard.RequireElevation();

            LogEventLevel level = Enum.TryParse<LogEventLevel>(config.Logging.Level, ignoreCase: true, out LogEventLevel parsed)
                ? parsed
                : LogEventLevel.Information;

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

                ApplyFrameworkLogPolicy(loggerConfig, config.Logging.FrameworkLevel);
            });

            builder.Services.ConfigureHttpJsonOptions(opts =>
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, LanceSharedJsonContext.Default));

            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<IUdpEndpointProbe, UdpEndpointProbe>();
            builder.Services.AddSingleton<IStreamingPortMap, ApolloStreamingPortMap>();
            builder.Services.AddSingleton<ISlotScanner, SlotScanner>();
            builder.Services.AddSingleton<ISlotAllocator, SlotAllocator>();
            builder.Services.AddSingleton<IProcessTracker, ProcessTracker>();
            builder.Services.AddSingleton<ISlotLifecycle, SlotLifecycle>();
            builder.Services.AddSingleton<ISessionRegistry, SessionRegistry>();
            builder.Services.AddSingleton<ISessionRecordStore, FileSessionRecordStore>();
            builder.Services.AddSingleton<IHookProcessRunner, ProcessHookRunner>();
            builder.Services.AddSingleton<HookLoader>();
            builder.Services.AddSingleton<HookDispatcher>();
            builder.Services.AddSingleton<ISessionOrchestrator, SessionOrchestrator>();
            builder.Services.AddSingleton<SessionReconciler>();
            builder.Services.AddTransient<BearerTokenMiddleware>();
            builder.Services.AddTransient<HttpBodyLoggingMiddleware>();

            // Drives session lifecycle from UDP-based client-connection detection:
            // marks Connected, and ends sessions on provision-timeout / probe-watch.
            builder.Services.AddHostedService<SessionDetectionService>();

            WebApplication app = builder.Build();

            AgentConfigValidator.Validate(config);

            // Startup narrative: emitted after Build() so it flows through the fully
            // configured logger (file sink included), not the console-only bootstrap.
            Log.Information("Lance agent {Version} starting", GetVersion());

            if (File.Exists(configPath))
                Log.Information("Config loaded from {ConfigPath}", configPath);
            else
                Log.Warning("Config file not found at {ConfigPath} — running with defaults", configPath);

            if (!string.IsNullOrEmpty(config.Auth?.Token))
                Log.Information("Bearer token authentication enabled");
            else
                Log.Warning("No auth token configured — agent API is open to all callers");

            IProcessTracker tracker = app.Services.GetRequiredService<IProcessTracker>();
            ISlotLifecycle lifecycle = app.Services.GetRequiredService<ISlotLifecycle>();

            Microsoft.Extensions.Logging.ILogger adoptLogger = app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ProcessAdopter).FullName!);
            ProcessAdopter.Adopt(config, tracker, adoptLogger);

            int adoptedCount = tracker.GetAll().Count;
            if (adoptedCount == 0)
                Log.Information("No running Apollo instances to adopt");
            else
                Log.Information("Adoption complete — {Count} running Apollo instance(s) adopted", adoptedCount);

            // Crash recovery: replay orphaned session teardowns / re-adopt live sessions
            // BEFORE the listener opens, so a fresh connect isn't clobbered by a replay.
            await app.Services.GetRequiredService<SessionReconciler>().ReconcileAsync();

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

            // Lance states its own bind address once the listener is up — the framework's
            // "Now listening on" banner is a Microsoft.* source and is filtered by default.
            app.Lifetime.ApplicationStarted.Register(() =>
                Log.Information("Listening on https://{Host}:{Port}", config.Listen.Host, config.Listen.Port));

            app.UseMiddleware<BearerTokenMiddleware>();

            if (level <= LogEventLevel.Debug)
            {
                app.UseMiddleware<HttpBodyLoggingMiddleware>();
            }

            app.MapHealthEndpoints(startedAt);
            app.MapSlotEndpoints();
            app.MapSessionEndpoints();

            await app.RunAsync();
            Log.Information("Lance agent stopped");
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

    // Framework (Microsoft.*) log sources feel foreign in a cross-platform tool. By
    // default ("off"/"none") they are dropped entirely; a real level re-admits them at
    // that floor for the rare case of debugging the web/TLS stack itself.
    private static void ApplyFrameworkLogPolicy(LoggerConfiguration loggerConfig, string frameworkLevel)
    {
        if (Enum.TryParse(frameworkLevel, ignoreCase: true, out LogEventLevel level))
        {
            loggerConfig.MinimumLevel.Override("Microsoft", level);
            return;
        }

        loggerConfig.Filter.ByExcluding(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source)
            && source is ScalarValue { Value: string context }
            && context.StartsWith("Microsoft", StringComparison.Ordinal));
    }

    private static string GetVersion()
    {
        Version? version = typeof(Program).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
