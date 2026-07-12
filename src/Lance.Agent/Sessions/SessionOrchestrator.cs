using System.Collections.Concurrent;
using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Lance.Hooks;
using Lance.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace Lance.Agent.Sessions;

internal sealed class SessionOrchestrator : ISessionOrchestrator
{
    private readonly AgentConfig _config;
    private readonly ISessionRegistry _registry;
    private readonly ISessionRecordStore _store;
    private readonly ISlotScanner _scanner;
    private readonly ISlotAllocator _allocator;
    private readonly ISlotLifecycle _lifecycle;
    private readonly HookLoader _hookLoader;
    private readonly HookDispatcher _dispatcher;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, SessionRecord> _records;
    private readonly SemaphoreSlim _handshakeLock;

    public SessionOrchestrator(
        AgentConfig config,
        ISessionRegistry registry,
        ISessionRecordStore store,
        ISlotScanner scanner,
        ISlotAllocator allocator,
        ISlotLifecycle lifecycle,
        HookLoader hookLoader,
        HookDispatcher dispatcher,
        ILogger<SessionOrchestrator> logger)
    {
        _config = config;
        _registry = registry;
        _store = store;
        _scanner = scanner;
        _allocator = allocator;
        _lifecycle = lifecycle;
        _hookLoader = hookLoader;
        _dispatcher = dispatcher;
        _logger = logger;
        _records = new ConcurrentDictionary<string, SessionRecord>(StringComparer.Ordinal);
        _handshakeLock = new SemaphoreSlim(1, 1);
    }

    public async Task<SessionCreationResult> CreateSessionAsync(string sessionId, int count, string clientIp, string agentIp, CancellationToken cancellationToken = default)
    {
        if (!SessionId.IsValid(sessionId))
        {
            return new SessionCreationResult { ErrorCode = "invalid_session_id", ErrorMessage = "Session id must be 1-64 characters of letters, digits, '-' or '_'.", HttpStatus = 400 };
        }

        if (count < 1)
        {
            return new SessionCreationResult { ErrorCode = "invalid_slot_id", ErrorMessage = "Requested slot count must be at least 1.", HttpStatus = 400 };
        }

        // Serialize handshakes so two concurrent connects cannot pick the same free
        // slots before either has claimed them in the registry.
        await _handshakeLock.WaitAsync(cancellationToken);
        try
        {
            if (_registry.TryGet(sessionId, out _))
            {
                return Conflict(sessionId);
            }

            (IReadOnlyList<SlotDto> slots, SessionCreationResult? failure) = await SelectAndStartSlotsAsync(count, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (slots.Count == 0)
            {
                return new SessionCreationResult { ErrorCode = "apollo_launch_failed", ErrorMessage = "No slots could be started for the session.", HttpStatus = 500 };
            }

            int[] slotIds = new int[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                slotIds[i] = slots[i].Id;
            }

            Session session = new()
            {
                Id = sessionId,
                ClientIp = clientIp,
                SlotIds = slotIds,
                State = SessionState.Provisioned,
                CreatedAt = DateTimeOffset.UtcNow
            };
            if (!_registry.TryAdd(session))
            {
                return Conflict(sessionId);
            }

            await PersistAndStartAsync(session, agentIp, cancellationToken);

            _logger.LogInformation("Session {SessionId}: provisioned {Count} slot(s) [{Slots}] for client {ClientIp}.",
                sessionId, slotIds.Length, string.Join(", ", slotIds), clientIp);

            return new SessionCreationResult { Slots = slots };
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    public async Task EndSessionAsync(string sessionId, string source, CancellationToken cancellationToken = default)
    {
        // Mark ended first so two detectors racing to end the same session run teardown
        // only once.
        if (!_registry.TryMarkEnded(sessionId))
        {
            return;
        }

        if (_records.TryRemove(sessionId, out SessionRecord? record))
        {
            await RunTeardownAsync(record, source, cancellationToken);
        }

        _registry.Remove(sessionId);
        _logger.LogInformation("Session {SessionId}: ended ({Source}); slots freed, Apollo left running.", sessionId, source);
    }

    public void Readopt(SessionRecord record)
    {
        Session session = new()
        {
            Id = record.SessionId,
            ClientIp = record.ClientIp,
            SlotIds = record.SlotIds,
            State = SessionState.Connected,
            CreatedAt = record.CreatedAt,
            ConnectedAt = DateTimeOffset.UtcNow
        };
        if (_registry.TryAdd(session))
        {
            _records[record.SessionId] = record;
            _logger.LogInformation("Session {SessionId}: re-adopted after restart; a client is still connected.", record.SessionId);
        }
    }

    public async Task ReplayTeardownAsync(SessionRecord record, string source, CancellationToken cancellationToken = default)
    {
        await RunTeardownAsync(record, source, cancellationToken);
        _logger.LogInformation("Session {SessionId}: orphaned teardown replayed ({Source}); record cleared.", record.SessionId, source);
    }

    private async Task RunTeardownAsync(SessionRecord record, string source, CancellationToken cancellationToken)
    {
        // Set event/source fresh (never restored from the snapshot) so a hook can tell a
        // replayed teardown from a live one. Teardown commands must be idempotent.
        Dictionary<string, string> env = new(record.Env, StringComparer.Ordinal)
        {
            [LanceEnv.Event] = LanceEvents.SessionEnded,
            [LanceEnv.EventSource] = source
        };

        await _dispatcher.RunResolvedAsync(record.TeardownCommands, env, cancellationToken);
        await _store.DeleteAsync(record.SessionId, cancellationToken);
    }

    private async Task PersistAndStartAsync(Session session, string agentIp, CancellationToken cancellationToken)
    {
        Dictionary<string, string> env = BuildEnv(LanceEvents.SessionStarted, "explicit", session, agentIp);
        IReadOnlyList<LoadedHook> hooks = LoadHooks();

        // Snapshot the resolved teardown and persist the record BEFORE running any
        // session_started hook, so a crash mid-setup still leaves a record to replay.
        IReadOnlyList<ResolvedCommand> teardown = _dispatcher.Resolve(LanceEvents.SessionEnded, hooks, env);
        SessionRecord record = new()
        {
            SessionId = session.Id,
            ClientIp = session.ClientIp,
            SlotIds = [.. session.SlotIds],
            TeardownCommands = [.. teardown],
            Env = env,
            CreatedAt = session.CreatedAt
        };
        _records[session.Id] = record;
        await _store.SaveAsync(record, cancellationToken);

        await _dispatcher.DispatchAsync(LanceEvents.SessionStarted, hooks, env, cancellationToken);
    }

    private async Task<(IReadOnlyList<SlotDto> Slots, SessionCreationResult? Failure)> SelectAndStartSlotsAsync(int count, CancellationToken cancellationToken)
    {
        List<SlotDto> free = FindFreeSlots();
        if (free.Count < count)
        {
            SessionCreationResult? allocFailure = TryAllocateMore(count - free.Count);
            if (allocFailure is not null)
            {
                return ([], allocFailure);
            }

            free = FindFreeSlots();
            if (free.Count < count)
            {
                return ([], new SessionCreationResult { ErrorCode = "no_free_slots", ErrorMessage = $"Only {free.Count} slot(s) available for {count} monitor(s).", HttpStatus = 409 });
            }
        }

        free.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        List<int> chosen = [];
        for (int i = 0; i < count; i++)
        {
            chosen.Add(free[i].Id);
        }

        await StartSlotsAsync(chosen, cancellationToken);

        // Re-scan so the response reflects the started slots (Running/Connected only —
        // partial success: a slot that failed to start is dropped).
        List<SlotDto> ready = [];
        HashSet<int> chosenSet = [.. chosen];
        foreach (SlotDto slot in _scanner.Scan())
        {
            if (chosenSet.Contains(slot.Id) && slot.Status is "Running" or "Connected")
            {
                ready.Add(slot);
            }
        }

        return (ready, null);
    }

    private List<SlotDto> FindFreeSlots()
    {
        HashSet<int> ownedByActiveSessions = [];
        foreach (Session session in _registry.GetAll())
        {
            foreach (int slotId in session.SlotIds)
            {
                ownedByActiveSessions.Add(slotId);
            }
        }

        List<SlotDto> free = [];
        foreach (SlotDto slot in _scanner.Scan())
        {
            bool available = !slot.IsAdopted && slot.Status != "Connected" && !ownedByActiveSessions.Contains(slot.Id);
            if (available)
            {
                free.Add(slot);
            }
        }

        return free;
    }

    private SessionCreationResult? TryAllocateMore(int deficit)
    {
        int standardCount = 0;
        foreach (SlotDto slot in _scanner.Scan())
        {
            if (!slot.IsAdopted)
            {
                standardCount++;
            }
        }

        AllocateResult result = _allocator.Allocate(standardCount + deficit);
        if (result.IsSuccess)
        {
            return null;
        }

        int status = result.ErrorCode is "template_missing" or "io_error" ? 500 : 409;
        string code = result.ErrorCode == "max_slots_exceeded" ? "no_free_slots" : result.ErrorCode!;
        return new SessionCreationResult { ErrorCode = code, ErrorMessage = result.ErrorMessage!, HttpStatus = status };
    }

    private async Task StartSlotsAsync(IReadOnlyList<int> slotIds, CancellationToken cancellationToken)
    {
        foreach (int slotId in slotIds)
        {
            LifecycleResult result = await _lifecycle.StartAsync(slotId, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Session setup: slot {SlotId} failed to start ({ErrorCode}); dropping it.", slotId, result.ErrorCode);
            }
        }
    }

    private IReadOnlyList<LoadedHook> LoadHooks()
    {
        List<HookFileRef> references = [];
        foreach (AgentHookEntry entry in _config.Hooks)
        {
            if (!string.IsNullOrWhiteSpace(entry.Path))
            {
                references.Add(new HookFileRef { Path = entry.Path, Active = entry.Active ?? true });
            }
        }

        return _hookLoader.Load(references);
    }

    private static SessionCreationResult Conflict(string sessionId)
    {
        return new SessionCreationResult { ErrorCode = "session_id_conflict", ErrorMessage = $"Session '{sessionId}' is already active.", HttpStatus = 409 };
    }

    private static Dictionary<string, string> BuildEnv(string eventName, string source, Session session, string agentIp)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LanceEnv.Event] = eventName,
            [LanceEnv.EventSource] = source,
            [LanceEnv.SessionId] = session.Id,
            [LanceEnv.Side] = "agent",
            [LanceEnv.AgentIp] = agentIp,
            [LanceEnv.ClientIp] = session.ClientIp,
            [LanceEnv.SlotIds] = string.Join(",", session.SlotIds)
        };
    }
}
