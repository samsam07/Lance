using System.CommandLine;
using Lance.Client.Commands;
using Lance.Client.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Lance.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Option<string?> agentOption = new("--agent", "-a") { Description = "Override the agent URL" };
        Option<string?> configOption = new("--config", "-c") { Description = "Path to lance.json config file" };
        Option<string?> tokenOption = new("--token", "-k") { Description = "Bearer token for agent authentication" };
        Option<bool> verboseOption = new("--verbose", "-v") { Description = "Enable debug output to stderr" };
        Option<bool> noColorOption = new("--no-color") { Description = "Disable ANSI color output" };

        RootCommand root = new("Lance — multi-monitor remote desktop orchestrator")
        {
            agentOption,
            configOption,
            tokenOption,
            verboseOption,
            noColorOption,
        };

        // config is loaded after parsing; commands close over this variable and
        // read its value during InvokeAsync — after it has been set below.
        ClientConfig? config = null;

        GlobalOptions globals = new(agentOption, tokenOption, noColorOption, () => config);

        root.Add(SlotsCommand.Build(globals));
        root.Add(StatusCommand.Build(globals));
        root.Add(AllocateCommand.Build(globals));
        root.Add(StartCommand.Build(globals));
        root.Add(StopCommand.Build(globals));
        root.Add(DeallocateCommand.Build(globals));
        root.Add(ForceDeallocateCommand.Build(globals));
        root.Add(ConfigCommand.Build(globals));
        root.Add(ConnectCommand.Build(globals));

        ParseResult parseResult = root.Parse(args);

        string? configPath = parseResult.GetValue(configOption);
        try
        {
            config = configPath is not null
                ? ClientConfigLoader.LoadFrom(configPath)
                : ClientConfigLoader.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading config: {ex.Message}");
            return ExitCodes.Generic;
        }

        bool verbose = parseResult.GetValue(verboseOption);
        bool noColor = parseResult.GetValue(noColorOption);
        SetupLogging(verbose, noColor, config?.Logging);

        try
        {
            return await parseResult.InvokeAsync();
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void SetupLogging(bool verbose, bool noColor, ClientLoggingConfig? logging)
    {
        LogEventLevel level = verbose
            ? LogEventLevel.Debug
            : Enum.TryParse<LogEventLevel>(logging?.Level, ignoreCase: true, out LogEventLevel parsed)
                ? parsed
                : LogEventLevel.Information;

        ConsoleTheme theme = noColor ? ConsoleTheme.None : AnsiConsoleTheme.Code;

        LoggerConfiguration logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose,
                theme: theme);

        if (logging?.FilePath is string filePath)
        {
            logConfig = logConfig.WriteTo.File(
                filePath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day);
        }

        Log.Logger = logConfig.CreateLogger();
    }
}
