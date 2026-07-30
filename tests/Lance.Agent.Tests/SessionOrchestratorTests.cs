using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Lance.Agent.Sessions;
using Lance.Hooks;
using Lance.Shared.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lance.Agent.Tests;

public sealed class SessionOrchestratorTests
{
    [Fact]
    public async Task CreateSession_InvalidId_Returns400()
    {
        using SessionTempDir dir = new();
        SessionOrchestrator orchestrator = BuildOrchestrator(new FakeSlots(), dir.Path, out _);

        SessionCreationResult result = await orchestrator.CreateSessionAsync("bad id!", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_session_id", result.ErrorCode);
        Assert.Equal(400, result.HttpStatus);
    }

    [Fact]
    public async Task CreateSession_HappyPath_StartsSlots_Registers_Persists()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out SessionRegistry registry);

        SessionCreationResult result = await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Slots);
        Assert.Equal("Running", result.Slots[0].Status);

        registry.TryGet("abc", out Session? session);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Provisioned, session.State);
        Assert.Equal([0], session.SlotIds);
        Assert.True(File.Exists(Path.Combine(dir.Path, "abc.json")));
    }

    [Fact]
    public async Task CreateSession_DuplicateId_Conflicts()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        slots.Seed(1, "Allocated");
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out _);

        await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);
        SessionCreationResult second = await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        Assert.False(second.IsSuccess);
        Assert.Equal("session_id_conflict", second.ErrorCode);
        Assert.Equal(409, second.HttpStatus);
    }

    [Fact]
    public async Task CreateSession_SkipsSlotsOwnedByAnotherSession()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        slots.Seed(1, "Allocated");
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out _);

        SessionCreationResult first = await orchestrator.CreateSessionAsync("aaa", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);
        SessionCreationResult second = await orchestrator.CreateSessionAsync("bbb", 1, "10.0.0.3", "10.0.0.2", TestContext.Current.CancellationToken);

        Assert.Equal(0, first.Slots[0].Id);
        Assert.Equal(1, second.Slots[0].Id);
    }

    [Fact]
    public async Task CreateSession_NoCapacity_ReturnsNoFreeSlots()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new(maxSlots: 1);
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out _);

        await orchestrator.CreateSessionAsync("aaa", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);
        SessionCreationResult second = await orchestrator.CreateSessionAsync("bbb", 1, "10.0.0.3", "10.0.0.2", TestContext.Current.CancellationToken);

        Assert.False(second.IsSuccess);
        Assert.Equal("no_free_slots", second.ErrorCode);
        Assert.Equal(409, second.HttpStatus);
    }

    [Fact]
    public async Task EndSession_RemovesSessionAndRecord()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out SessionRegistry registry);
        await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        await orchestrator.EndSessionAsync("abc", "probe_watch", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(registry.TryGet("abc", out _));
        Assert.False(File.Exists(Path.Combine(dir.Path, "abc.json")));
    }

    [Fact]
    public async Task EndSession_StopsTheSessionSlots()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out _);
        SessionCreationResult created = await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);
        Assert.Equal("Running", created.Slots[0].Status);

        await orchestrator.EndSessionAsync("abc", "ping", cancellationToken: TestContext.Current.CancellationToken);

        // Ending the session stops its slot's Apollo — even slot 0 (the template) — so
        // the virtual display is torn down rather than left running.
        SlotDto slot0 = slots.Scan()[0];
        Assert.Equal(0, slot0.Id);
        Assert.Equal("Allocated", slot0.Status);
        Assert.Null(slot0.ProcessId);
    }

    [Fact]
    public async Task EndSession_KeepRunning_FreesSessionButLeavesSlotRunning()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out SessionRegistry registry);
        await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        await orchestrator.EndSessionAsync("abc", "ping", keepRunning: true, TestContext.Current.CancellationToken);

        // --keep-running: the session is freed but its Apollo stays up for a fast reconnect.
        Assert.False(registry.TryGet("abc", out _));
        Assert.Equal("Running", slots.Scan()[0].Status);
    }

    [Fact]
    public async Task EndSession_FreesSlotForReuse_NoNewAllocation()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out _);

        SessionCreationResult first = await orchestrator.CreateSessionAsync("aaa", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);
        await orchestrator.EndSessionAsync("aaa", "ping", cancellationToken: TestContext.Current.CancellationToken);
        SessionCreationResult second = await orchestrator.CreateSessionAsync("bbb", 1, "10.0.0.3", "10.0.0.2", TestContext.Current.CancellationToken);

        // The ended session freed slot 0, so the next connect reuses it — the pool must
        // not grow a fresh slot while a freed one is available.
        Assert.Equal(0, first.Slots[0].Id);
        Assert.Equal(0, second.Slots[0].Id);
        Assert.Single(slots.Scan());
    }

    [Fact]
    public async Task EndSession_TeardownThrows_StillFreesSession()
    {
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Allocated", isTemplate: true);
        SessionOrchestrator orchestrator = BuildOrchestrator(slots, dir.Path, out SessionRegistry registry, new ThrowingDeleteStore());
        await orchestrator.CreateSessionAsync("abc", 1, "10.0.0.1", "10.0.0.2", TestContext.Current.CancellationToken);

        // A failing teardown must never pin the slot: the session is still freed.
        await orchestrator.EndSessionAsync("abc", "ping", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(registry.TryGet("abc", out _));
    }

    private static SessionOrchestrator BuildOrchestrator(FakeSlots slots, string recordDir, out SessionRegistry registry, ISessionRecordStore? store = null)
    {
        AgentConfig config = new()
        {
            Sessions = new SessionsConfig { RecordDir = recordDir, ProvisionGraceSeconds = 30, ProbePollSeconds = 1 },
            Hooks = []
        };
        registry = new SessionRegistry();
        ISessionRecordStore recordStore = store ?? new FileSessionRecordStore(config, NullLogger<FileSessionRecordStore>.Instance);
        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        HookDispatcher dispatcher = new(new ProcessHookRunner(), NullLogger<HookDispatcher>.Instance);
        return new SessionOrchestrator(config, registry, recordStore, slots, slots, slots, loader, dispatcher, NullLogger<SessionOrchestrator>.Instance);
    }
}

// A record store whose teardown delete always fails, to prove EndSession frees the
// session even when teardown throws.
internal sealed class ThrowingDeleteStore : ISessionRecordStore
{
    public Task SaveAsync(SessionRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<SessionRecord>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionRecord>>([]);

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
        throw new IOException("delete failed");
}

// A fake pool backing the scanner, allocator, and lifecycle so the orchestrator's
// free-slot selection and start flow can be exercised without real Apollo/disk state.
internal sealed class FakeSlots : ISlotScanner, ISlotAllocator, ISlotLifecycle
{
    private readonly Dictionary<int, SlotDto> _slots;
    private readonly int _maxSlots;
    private int _nextPid;

    public FakeSlots(int maxSlots = 8)
    {
        _slots = [];
        _maxSlots = maxSlots;
        _nextPid = 1000;
    }

    public void Seed(int id, string status, bool isTemplate = false)
    {
        _slots[id] = Build(id, status, isTemplate);
    }

    public IReadOnlyList<SlotDto> Scan()
    {
        List<SlotDto> result = [.. _slots.Values];
        result.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return result;
    }

    public AllocateResult Allocate(int count)
    {
        if (count > _maxSlots)
        {
            return new AllocateResult { ErrorCode = "max_slots_exceeded", ErrorMessage = $"Cannot exceed {_maxSlots} slots." };
        }

        for (int id = 0; id < count; id++)
        {
            if (!_slots.ContainsKey(id))
            {
                _slots[id] = Build(id, "Allocated", isTemplate: false);
            }
        }

        return new AllocateResult { Slots = Scan() };
    }

    public DeallocateResult Deallocate(int id)
    {
        _slots.Remove(id);
        return new DeallocateResult();
    }

    public Task<LifecycleResult> StartAsync(int slotId, CancellationToken cancellationToken = default)
    {
        if (_slots.TryGetValue(slotId, out SlotDto? slot) && slot.Status == "Allocated")
        {
            _slots[slotId] = slot with { Status = "Running", ProcessId = _nextPid++ };
        }

        return Task.FromResult(new LifecycleResult());
    }

    public Task<LifecycleResult> StopAsync(int slotId, CancellationToken cancellationToken = default)
    {
        if (_slots.TryGetValue(slotId, out SlotDto? slot) && slot.Status is "Running" or "Connected")
        {
            _slots[slotId] = slot with { Status = "Allocated", ProcessId = null };
        }

        return Task.FromResult(new LifecycleResult());
    }

    private SlotDto Build(int id, string status, bool isTemplate)
    {
        bool running = status is "Running" or "Connected";
        return new SlotDto
        {
            Id = id,
            Name = $"Lance-{id}",
            Host = "host",
            Port = 47989 - (id * 1000),
            Status = status,
            ConfigPath = $"sunshine_{id}.conf",
            ConfigName = $"sunshine_{id}.conf",
            IsTemplate = isTemplate,
            IsAdopted = false,
            AllocatedAt = DateTimeOffset.UtcNow,
            ProcessId = running ? _nextPid++ : null
        };
    }
}
