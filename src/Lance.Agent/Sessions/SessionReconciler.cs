using Lance.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Lance.Agent.Sessions;

// Startup crash recovery (Slice 1.5). A session record on disk means teardown never
// ran (agent crashed mid-session). For each surviving record: if any of its slots is
// still streaming, the session is alive — re-adopt it; otherwise it is orphaned —
// replay the snapshotted teardown and delete the record. Runs BEFORE the HTTP listener
// opens so a fresh connect cannot be clobbered by a replayed teardown.
internal sealed class SessionReconciler
{
    private readonly ISessionRecordStore _store;
    private readonly ISlotScanner _scanner;
    private readonly IUdpEndpointProbe _probe;
    private readonly IStreamingPortMap _portMap;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly ILogger<SessionReconciler> _logger;

    public SessionReconciler(
        ISessionRecordStore store,
        ISlotScanner scanner,
        IUdpEndpointProbe probe,
        IStreamingPortMap portMap,
        ISessionOrchestrator orchestrator,
        ILogger<SessionReconciler> logger)
    {
        _store = store;
        _scanner = scanner;
        _probe = probe;
        _portMap = portMap;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionRecord> records = await _store.LoadAllAsync(cancellationToken);
        if (records.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Recovering {Count} session record(s) left by a previous run.", records.Count);
        IReadOnlyDictionary<int, bool> slotConnected = SlotConnectivity.Snapshot(_scanner, _probe, _portMap);

        foreach (SessionRecord record in records)
        {
            if (IsAnySlotConnected(record, slotConnected))
            {
                _orchestrator.Readopt(record);
            }
            else
            {
                _logger.LogInformation("Session {SessionId}: no client after restart; running its teardown.", record.SessionId);
                await _orchestrator.ReplayTeardownAsync(record, "reconcile", cancellationToken);
            }
        }
    }

    private static bool IsAnySlotConnected(SessionRecord record, IReadOnlyDictionary<int, bool> slotConnected)
    {
        foreach (int slotId in record.SlotIds)
        {
            if (slotConnected.TryGetValue(slotId, out bool connected) && connected)
            {
                return true;
            }
        }

        return false;
    }
}
