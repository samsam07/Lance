using Lance.Agent.Configuration;
using Lance.Shared.Dtos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lance.Agent.Services;

// [VALIDATE-UDP] (Phase 3 Slice 6.1). Diagnostic watcher: polls the UDP endpoint
// table and logs when a slot gains or loses a connected client, derived from the
// Apollo process holding its streaming UDP ports. This lets the detection mechanism
// be validated by reading Lance's own logs during a real stream (clean connect /
// clean disconnect / hard NIC-cut). It does NOT drive session state yet — that is
// Slice 6.4. Windows-only for now ([VERIFY-APOLLO] Linux).
internal sealed class UdpProbeWatchService : BackgroundService
{
    private readonly ISlotScanner _scanner;
    private readonly IUdpEndpointProbe _probe;
    private readonly IStreamingPortMap _portMap;
    private readonly ILogger<UdpProbeWatchService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<int, bool> _lastConnected;
    private readonly Dictionary<int, string> _lastForeignHolders;

    public UdpProbeWatchService(
        AgentConfig config,
        ISlotScanner scanner,
        IUdpEndpointProbe probe,
        IStreamingPortMap portMap,
        ILogger<UdpProbeWatchService> logger)
    {
        _scanner = scanner;
        _probe = probe;
        _portMap = portMap;
        _logger = logger;
        int seconds = config.Sessions.ProbePollSeconds > 0 ? config.Sessions.ProbePollSeconds : 1;
        _pollInterval = TimeSpan.FromSeconds(seconds);
        _lastConnected = [];
        _lastForeignHolders = [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("Detecting client connections is only supported on Windows; this feature is turned off on this computer.");
            return;
        }

        _logger.LogInformation("Watching slots for client connections (checking every {Interval} second(s)).", _pollInterval.TotalSeconds);

        using PeriodicTimer timer = new(_pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not check slots for client connections this time; will try again.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        IReadOnlyList<SlotDto> slots = _scanner.Scan();
        IReadOnlyDictionary<int, IReadOnlySet<int>> byProcess = _probe.SnapshotByPid();

        HashSet<int> runningSlotIds = [];
        foreach (SlotDto slot in slots)
        {
            if (slot.ProcessId is not int processId)
            {
                continue;   // not running → no streaming ports to watch
            }

            runningSlotIds.Add(slot.Id);
            EvaluateSlot(slot, processId, byProcess);
        }

        ForgetStoppedSlots(runningSlotIds);
    }

    private void EvaluateSlot(SlotDto slot, int processId, IReadOnlyDictionary<int, IReadOnlySet<int>> byProcess)
    {
        StreamingUdpPorts resolved = _portMap.Resolve(slot.Port);
        IReadOnlySet<int> heldByApollo = byProcess.TryGetValue(processId, out IReadOnlySet<int>? held) ? held : new HashSet<int>();

        IReadOnlyList<int> activePorts = resolved.ActivePorts(heldByApollo);
        bool connected = activePorts.Count > 0;

        ReportPortsHeldElsewhere(slot, processId, resolved, byProcess);

        bool hadState = _lastConnected.TryGetValue(slot.Id, out bool wasConnected);
        if (hadState && wasConnected == connected)
        {
            return;   // no change since last check — stay quiet
        }

        _lastConnected[slot.Id] = connected;

        // The first time we see a running slot with no client yet is not a
        // disconnection — note it quietly rather than announcing a change.
        if (!hadState && !connected)
        {
            _logger.LogDebug(
                "Slot {SlotId} is running but no client has connected yet (Apollo process {ProcessId}, port {Port}).",
                slot.Id, processId, slot.Port);
            return;
        }

        if (connected)
        {
            _logger.LogInformation(
                "Slot {SlotId}: a client connected — streaming on ports {Ports} (Apollo process {ProcessId}).",
                slot.Id, string.Join(", ", activePorts), processId);
            _logger.LogDebug(
                "Slot {SlotId}: Apollo process {ProcessId} is currently using UDP ports {Ports}.",
                slot.Id, processId, string.Join(", ", heldByApollo));
        }
        else
        {
            _logger.LogInformation(
                "Slot {SlotId}: the client disconnected (Apollo process {ProcessId}).",
                slot.Id, processId);
        }
    }

    private void ReportPortsHeldElsewhere(SlotDto slot, int apolloProcessId, StreamingUdpPorts resolved, IReadOnlyDictionary<int, IReadOnlySet<int>> byProcess)
    {
        // A streaming port held by a process other than the slot's Apollo process
        // usually means the stream lives on a helper/child process. Surface it (only
        // when it changes) so validation catches endpoints we might otherwise miss.
        List<string> holders = [];
        foreach (int port in resolved.Ports)
        {
            foreach (KeyValuePair<int, IReadOnlySet<int>> entry in byProcess)
            {
                if (entry.Key != apolloProcessId && entry.Value.Contains(port))
                {
                    holders.Add($"port {port} used by process {entry.Key}");
                }
            }
        }

        string signature = string.Join("; ", holders);
        string previous = _lastForeignHolders.GetValueOrDefault(slot.Id, string.Empty);
        if (signature == previous)
        {
            return;
        }

        _lastForeignHolders[slot.Id] = signature;
        if (holders.Count > 0)
        {
            _logger.LogDebug(
                "Slot {SlotId}: some streaming ports are used by another process, not its Apollo process {ProcessId} ({Holders}).",
                slot.Id, apolloProcessId, signature);
        }
    }

    private void ForgetStoppedSlots(HashSet<int> runningSlotIds)
    {
        // Drop remembered state for slots that are no longer running so a later reuse
        // is reported from a clean start.
        List<int> stopped = [];
        foreach (int slotId in _lastConnected.Keys)
        {
            if (!runningSlotIds.Contains(slotId))
            {
                stopped.Add(slotId);
            }
        }

        foreach (int slotId in stopped)
        {
            _lastConnected.Remove(slotId);
            _lastForeignHolders.Remove(slotId);
        }
    }
}
