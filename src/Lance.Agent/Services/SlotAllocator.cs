using System.Net;
using Lance.Agent.Configuration;
using Lance.Agent.Infrastructure;
using Lance.Shared.Dtos;

namespace Lance.Agent.Services;

internal sealed class SlotAllocator : ISlotAllocator
{
    private readonly AgentConfig _config;
    private readonly ISlotScanner _scanner;
    private readonly IProcessTracker _tracker;
    private readonly ILogger<SlotAllocator> _logger;
    private readonly string _resolvedHost;

    public SlotAllocator(AgentConfig config, ISlotScanner scanner, IProcessTracker tracker, ILogger<SlotAllocator> logger)
    {
        _config = config;
        _scanner = scanner;
        _tracker = tracker;
        _logger = logger;
        _resolvedHost = ResolveHost(config.Listen.Host);
    }

    private static string ResolveHost(string configuredHost)
    {
        return string.IsNullOrEmpty(configuredHost) || configuredHost == "0.0.0.0" || configuredHost == "*"
            ? Dns.GetHostName()
            : configuredHost;
    }

    public AllocateResult Allocate(int count)
    {
        _logger.LogInformation("Allocating to count {Count}", count);

        if (count < 1)
        {
            _logger.LogWarning("Allocation failed: {ErrorCode} — {ErrorMessage}", "invalid_slot_id", "Count must be at least 1.");
            return new AllocateResult
            {
                ErrorCode = "invalid_slot_id",
                ErrorMessage = "Count must be at least 1."
            };
        }

        if (count > _config.Slots.MaxCount)
        {
            _logger.LogWarning("Allocation failed: {ErrorCode} — {ErrorMessage}", "max_slots_exceeded", $"Count {count} exceeds the maximum of {_config.Slots.MaxCount}.");
            return new AllocateResult
            {
                ErrorCode = "max_slots_exceeded",
                ErrorMessage = $"Count {count} exceeds the maximum of {_config.Slots.MaxCount}."
            };
        }

        string templatePath = Path.Combine(_config.RemoteServer.ConfigDir, _config.RemoteServer.TemplateConfigName);
        if (!File.Exists(templatePath))
        {
            _logger.LogWarning("Allocation failed: {ErrorCode} — {ErrorMessage}", "template_missing", $"Template config not found: {templatePath}");
            return new AllocateResult
            {
                ErrorCode = "template_missing",
                ErrorMessage = $"Template config not found: {templatePath}"
            };
        }

        Dictionary<string, string> templateValues = InitializationFileReader.Read(templatePath);
        if (!int.TryParse(templateValues.GetValueOrDefault("port", ""), out int templatePort))
        {
            _logger.LogWarning("Allocation failed: {ErrorCode} — {ErrorMessage}", "template_missing", "Template config does not contain a valid 'port' value.");
            return new AllocateResult
            {
                ErrorCode = "template_missing",
                ErrorMessage = "Template config does not contain a valid 'port' value."
            };
        }

        IReadOnlyList<SlotDto> existing = _scanner.Scan();
        Dictionary<int, SlotDto> existingById = [];
        foreach (SlotDto s in existing)
        {
            existingById[s.Id] = s;
        }

        List<SlotDto> allSlots = [];

        if (existingById.TryGetValue(0, out SlotDto? template))
        {
            allSlots.Add(template);
        }

        try
        {
            for (int id = 1; id < count; id++)
            {
                if (existingById.TryGetValue(id, out SlotDto? existingSlot))
                {
                    _logger.LogDebug("Slot {SlotId} already exists, skipping clone", id);
                    allSlots.Add(existingSlot);
                    continue;
                }

                CloneTemplate(id, templatePort);
                int slotPort = templatePort - id * _config.Slots.PortStep;
                string configPath = Path.Combine(_config.RemoteServer.ConfigDir, BuildConfigName(id));
                allSlots.Add(BuildCreatedSlot(id, configPath, slotPort));
                _logger.LogInformation("Cloned slot {SlotId} at port {Port}", id, slotPort);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Allocation failed: {ErrorCode} — {ErrorMessage}", "io_error", ex.Message);
            return new AllocateResult
            {
                ErrorCode = "io_error",
                ErrorMessage = ex.Message
            };
        }

        _logger.LogInformation("Allocation complete — {Count} slot(s) in pool", allSlots.Count);
        return new AllocateResult { Slots = allSlots };
    }

    private string BuildConfigName(int id)
    {
        return _config.Slots.ConfigNamePattern.Replace("{id}", id.ToString());
    }

    private void CloneTemplate(int id, int templatePort)
    {
        string templatePath = Path.Combine(_config.RemoteServer.ConfigDir, _config.RemoteServer.TemplateConfigName);
        string[] templateLines = File.ReadAllLines(templatePath);

        int slotPort = templatePort - id * _config.Slots.PortStep;
        string logFileName = $"sunshine_{id}.log";

        HashSet<string> written = new(StringComparer.Ordinal);
        List<string> outputLines = new(templateLines.Length + 5);

        foreach (string line in templateLines)
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex > 0)
            {
                string normalizedKey = line[..equalsIndex].Trim().ToLowerInvariant();
                string? mutatedValue = GetMutatedValue(normalizedKey, id, slotPort, logFileName);
                if (mutatedValue is not null)
                {
                    outputLines.Add($"{normalizedKey} = {mutatedValue}");
                    written.Add(normalizedKey);
                    continue;
                }
            }

            outputLines.Add(line);
        }

        AppendIfMissing("sunshine_name", written, outputLines, $"{_config.Slots.NamePrefix}-{id}");
        AppendIfMissing("port", written, outputLines, slotPort.ToString());
        AppendIfMissing("log_path", written, outputLines, logFileName);
        AppendIfMissing("server_cmd", written, outputLines, "[]");
        AppendIfMissing("stream_audio", written, outputLines, "disabled");

        string clonePath = Path.Combine(_config.RemoteServer.ConfigDir, BuildConfigName(id));
        File.WriteAllLines(clonePath, outputLines);
    }

    private string? GetMutatedValue(string normalizedKey, int id, int slotPort, string logFileName)
    {
        return normalizedKey switch
        {
            "sunshine_name" => $"{_config.Slots.NamePrefix}-{id}",
            "port" => slotPort.ToString(),
            "log_path" => logFileName,
            "server_cmd" => "[]",
            "stream_audio" => "disabled",
            _ => null
        };
    }

    private static void AppendIfMissing(string key, HashSet<string> written, List<string> lines, string value)
    {
        if (!written.Contains(key))
        {
            lines.Add($"{key} = {value}");
        }
    }

    private SlotDto BuildCreatedSlot(int id, string configPath, int slotPort)
    {
        return new SlotDto
        {
            Id = id,
            Name = $"{_config.Slots.NamePrefix}-{id}",
            Host = _resolvedHost,
            Port = slotPort,
            Status = "Allocated",
            ConfigPath = configPath,
            ConfigName = Path.GetFileName(configPath),
            IsTemplate = false,
            IsAdopted = false,
            AllocatedAt = DateTimeOffset.UtcNow
        };
    }

    public DeallocateResult Deallocate(int id)
    {
        _logger.LogInformation("Deallocating slot {SlotId}", id);

        if (id == 0)
        {
            _logger.LogWarning("Deallocation refused for slot {SlotId}: {ErrorCode}", id, "cannot_deallocate_template");
            return new DeallocateResult
            {
                ErrorCode = "cannot_deallocate_template",
                ErrorMessage = "Slot 0 is the template and cannot be deallocated.",
                HttpStatus = 409
            };
        }

        IReadOnlyList<SlotDto> slots = _scanner.Scan();
        SlotDto? slot = null;

        foreach (SlotDto s in slots)
        {
            if (s.Id == id)
            {
                slot = s;
                break;
            }
        }

        if (slot is null)
        {
            _logger.LogDebug("Slot {SlotId} not found — treating as already deallocated (idempotent)", id);
            return new DeallocateResult { HttpStatus = 200 };
        }

        if (slot.IsAdopted)
        {
            _logger.LogWarning("Deallocation refused for slot {SlotId}: {ErrorCode}", id, "cannot_deallocate_adopted");
            return new DeallocateResult
            {
                ErrorCode = "cannot_deallocate_adopted",
                ErrorMessage = $"Slot {id} is an adopted slot and cannot be deallocated.",
                HttpStatus = 409
            };
        }

        bool isRunning = _tracker.TryGet(id, out SlotProcess? proc) && ProcessHelper.IsAlive(proc!.Pid);
        if (isRunning)
        {
            _logger.LogWarning("Deallocation refused for slot {SlotId}: {ErrorCode}", id, "slot_in_use");
            return new DeallocateResult
            {
                ErrorCode = "slot_in_use",
                ErrorMessage = $"Slot {id} is running. Stop it first or use force-deallocate.",
                HttpStatus = 409
            };
        }

        File.Delete(slot.ConfigPath);

        string logPath = Path.Combine(_config.RemoteServer.ConfigDir, $"sunshine_{id}.log");
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        _logger.LogInformation("Slot {SlotId} deallocated", id);
        return new DeallocateResult { HttpStatus = 200 };
    }
}
