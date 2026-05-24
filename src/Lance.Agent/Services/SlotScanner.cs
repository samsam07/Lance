using System.Net;
using Lance.Agent.Configuration;
using Lance.Agent.Infrastructure;
using Lance.Shared.Dtos;

namespace Lance.Agent.Services;

internal sealed class SlotScanner : ISlotScanner
{
    private readonly AgentConfig _config;
    private readonly IProcessTracker _tracker;
    private readonly string _resolvedHost;

    public SlotScanner(AgentConfig config, IProcessTracker tracker)
    {
        _config = config;
        _tracker = tracker;
        _resolvedHost = ResolveHost(config.Listen.Host);
    }

    private static string ResolveHost(string configuredHost)
    {
        return string.IsNullOrEmpty(configuredHost) || configuredHost == "0.0.0.0" || configuredHost == "*"
            ? Dns.GetHostName()
            : configuredHost;
    }

    public IReadOnlyList<SlotDto> Scan()
    {
        List<SlotDto> slots = [];

        string templatePath = Path.Combine(_config.RemoteServer.ConfigDir, _config.RemoteServer.TemplateConfigName);
        if (File.Exists(templatePath))
        {
            slots.Add(BuildSlot(0, templatePath, isTemplate: true));
        }

        foreach (string filePath in Directory.EnumerateFiles(_config.RemoteServer.ConfigDir))
        {
            string fileName = Path.GetFileName(filePath);
            if (!TryParseCloneId(fileName, out int id))
                continue;

            slots.Add(BuildSlot(id, filePath, isTemplate: false));
        }

        // Non-standard adopted slots live only in the PID table (id >= 1000).
        foreach ((int slotId, SlotProcess entry) in _tracker.GetAll())
        {
            if (slotId < 1000) continue;
            if (!ProcessHelper.IsAlive(entry.Pid)) continue;

            slots.Add(new SlotDto
            {
                Id = slotId,
                Name = entry.ConfigName.Length > 0 ? entry.ConfigName : $"Adopted-{slotId}",
                Host = _resolvedHost,
                Port = entry.ObservedPort,
                Status = "Running",
                ConfigPath = entry.ConfigPath,
                ConfigName = entry.ConfigName,
                IsTemplate = false,
                IsAdopted = true,
                ProcessId = entry.Pid,
                StartedAt = entry.StartedAt,
                AllocatedAt = entry.StartedAt
            });
        }

        slots.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return slots;
    }

    private SlotDto BuildSlot(int id, string filePath, bool isTemplate)
    {
        Dictionary<string, string> configValues = InitializationFileReader.Read(filePath);
        _ = int.TryParse(configValues.GetValueOrDefault("port", "0"), out int port);

        string name = isTemplate
            ? _config.Slots.TemplateName
            : $"{_config.Slots.NamePrefix}-{id}";

        string status = "Allocated";
        int? processId = null;
        DateTimeOffset? startedAt = null;

        if (!isTemplate && _tracker.TryGet(id, out SlotProcess? entry) && ProcessHelper.IsAlive(entry!.Pid))
        {
            status = "Running";
            processId = entry.Pid;
            startedAt = entry.StartedAt;
        }

        return new SlotDto
        {
            Id = id,
            Name = name,
            Host = _resolvedHost,
            Port = port,
            Status = status,
            ConfigPath = filePath,
            ConfigName = Path.GetFileName(filePath),
            IsTemplate = isTemplate,
            IsAdopted = false,
            ProcessId = processId,
            StartedAt = startedAt,
            AllocatedAt = new DateTimeOffset(File.GetCreationTimeUtc(filePath), TimeSpan.Zero)
        };
    }

    private bool TryParseCloneId(string fileName, out int id)
    {
        id = 0;
        string pattern = _config.Slots.ConfigNamePattern;
        int placeholderIndex = pattern.IndexOf("{id}", StringComparison.Ordinal);
        if (placeholderIndex < 0)
            return false;

        string prefix = pattern[..placeholderIndex];
        string suffix = pattern[(placeholderIndex + 4)..];

        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        return int.TryParse(fileName[prefix.Length..^suffix.Length], out id);
    }
}
