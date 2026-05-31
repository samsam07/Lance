using System.Diagnostics;
using System.Text;
using Lance.Agent.Configuration;
using Lance.Agent.Services;

namespace Lance.Agent.Infrastructure;

internal static class ProcessAdopter
{
    public static void Adopt(AgentConfig config, IProcessTracker tracker, ILogger logger)
    {
        if (OperatingSystem.IsLinux())
        {
            AdoptLinux(config, tracker, logger);
        }
        else if (OperatingSystem.IsWindows())
        {
            AdoptWindows(config, tracker, logger);
        }
    }

    private static void AdoptLinux(AgentConfig config, IProcessTracker tracker, ILogger logger)
    {
        string configuredExe = Path.GetFileName(config.RemoteServer.Executable);
        logger.LogDebug("Scanning /proc for running {ExecutableName} instances", configuredExe);

        foreach (string procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out int pid))
                continue;

            string[] argv;
            try
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(procDir, "cmdline"));
                string raw = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                if (raw.Length == 0) continue;
                argv = raw.Split('\0');
            }
            catch
            {
                logger.LogDebug("Could not read /proc/{Pid}/cmdline — skipping", pid);
                continue;
            }

            if (!string.Equals(Path.GetFileName(argv[0]), configuredExe, StringComparison.OrdinalIgnoreCase))
                continue;

            string? configPath = FindConfigArg(argv, config.RemoteServer.ConfigDir);
            AdoptProcess(config, tracker, logger, pid, configPath);
        }
    }

    private static void AdoptWindows(AgentConfig config, IProcessTracker tracker, ILogger logger)
    {
        string exeName = Path.GetFileNameWithoutExtension(config.RemoteServer.Executable);
        logger.LogDebug("Scanning for running {ExecutableName} instances", exeName);

        Process[] processes = Process.GetProcessesByName(exeName);
        foreach (Process proc in processes)
        {
            int pid = proc.Id;
            proc.Dispose();

            string? commandLine = NativeMethods.ReadProcessCommandLine(pid);
            if (commandLine is null)
            {
                logger.LogDebug("Could not read process info for PID {Pid} — skipping adoption", pid);
                continue;
            }

            string[] argv = SplitCommandLine(commandLine);
            string? configPath = FindConfigArg(argv, config.RemoteServer.ConfigDir);
            AdoptProcess(config, tracker, logger, pid, configPath);
        }
    }

    private static void AdoptProcess(
        AgentConfig config, IProcessTracker tracker, ILogger logger,
        int pid, string? configPath)
    {
        // Resolve effective config path for port reading.
        // null configPath means Apollo was launched with no explicit --config arg,
        // so it is using the default sunshine.conf (= Slot 0).
        string templateConfigPath = Path.Combine(config.RemoteServer.ConfigDir, config.RemoteServer.TemplateConfigName);
        string portConfigPath = configPath ?? templateConfigPath;

        int port = 0;
        if (File.Exists(portConfigPath))
        {
            Dictionary<string, string> values = InitializationFileReader.Read(portConfigPath);
            port = int.TryParse(values.GetValueOrDefault("port", ""), out int parsedPort)
                ? parsedPort
                : SunshineDefaults.StreamingPort;
        }

        DateTimeOffset startedAt = GetStartTime(pid);

        if (configPath is not null
            && TryParseSlotId(Path.GetFileName(configPath), config.Slots.ConfigNamePattern, out int slotId))
        {
            // Standard clone slot: sunshine_1.conf, sunshine_2.conf, ...
            if (!tracker.TryGet(slotId, out _))
            {
                tracker.Add(slotId, new SlotProcess
                {
                    Pid = pid,
                    StartedAt = startedAt,
                    ObservedPort = port,
                    ConfigPath = configPath,
                    ConfigName = Path.GetFileName(configPath)
                });
                logger.LogInformation(
                    "Adopted standard slot {SlotId} (PID {Pid}, port {Port}, config {ConfigName})",
                    slotId, pid, port, Path.GetFileName(configPath));
            }
        }
        else if (configPath is null
            || string.Equals(Path.GetFileName(configPath), config.RemoteServer.TemplateConfigName, StringComparison.OrdinalIgnoreCase))
        {
            // No explicit config arg (default = sunshine.conf) OR explicit sunshine.conf → Slot 0.
            if (!tracker.TryGet(0, out _))
            {
                tracker.Add(0, new SlotProcess
                {
                    Pid = pid,
                    StartedAt = startedAt,
                    ObservedPort = port,
                    ConfigPath = templateConfigPath,
                    ConfigName = config.RemoteServer.TemplateConfigName
                });
                logger.LogInformation(
                    "Adopted template slot 0 (PID {Pid}, port {Port}, config {ConfigName})",
                    pid, port, config.RemoteServer.TemplateConfigName);
            }
        }
        else
        {
            // Truly non-standard: different config file not matching any known pattern.
            int adoptedId = NextAdoptedId(tracker);
            tracker.Add(adoptedId, new SlotProcess
            {
                Pid = pid,
                StartedAt = startedAt,
                ObservedPort = port,
                ConfigPath = configPath,
                ConfigName = Path.GetFileName(configPath)
            });
            logger.LogInformation(
                "Adopted non-standard slot {SlotId} (PID {Pid}, config {ConfigName})",
                adoptedId, pid, Path.GetFileName(configPath));
        }
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        List<string> args = new();
        int i = 0;

        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && commandLine[i] == ' ')
                i++;

            if (i >= commandLine.Length)
                break;

            StringBuilder current = new();
            bool inQuote = false;

            while (i < commandLine.Length && (inQuote || commandLine[i] != ' '))
            {
                if (commandLine[i] == '"')
                {
                    inQuote = !inQuote;
                    i++;
                }
                else
                {
                    current.Append(commandLine[i++]);
                }
            }

            if (current.Length > 0)
                args.Add(current.ToString());
        }

        return args.ToArray();
    }

    private static string? FindConfigArg(string[] argv, string configDir)
    {
        // Apollo/Sunshine takes the config path as a positional argument.
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                && Path.IsPathRooted(arg)
                && arg.StartsWith(configDir, StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }
        return null;
    }

    private static bool TryParseSlotId(string fileName, string pattern, out int id)
    {
        id = 0;
        int placeholderIndex = pattern.IndexOf("{id}", StringComparison.Ordinal);
        if (placeholderIndex < 0) return false;

        string prefix = pattern[..placeholderIndex];
        string suffix = pattern[(placeholderIndex + 4)..];

        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        return int.TryParse(fileName[prefix.Length..^suffix.Length], out id);
    }

    private static DateTimeOffset GetStartTime(int pid)
    {
        try
        {
            return new DateTimeOffset(Process.GetProcessById(pid).StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static int NextAdoptedId(IProcessTracker tracker)
    {
        int max = 999;
        foreach ((int slotId, _) in tracker.GetAll())
        {
            if (slotId >= 1000 && slotId > max)
                max = slotId;
        }
        return max + 1;
    }
}
