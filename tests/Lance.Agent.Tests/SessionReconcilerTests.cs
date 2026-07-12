using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Lance.Agent.Sessions;
using Lance.Hooks;
using Lance.Shared.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lance.Agent.Tests;

public sealed class SessionReconcilerTests
{
    [Fact]
    public async Task Reconcile_OrphanRecord_ReplaysTeardownAndDeletesRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FakeSlots slots = new();   // no running slots → slot 0 reads as not connected

        SessionReconciler reconciler = BuildReconciler(slots, new FakeUdpEndpointProbe(), dir.Path, out SessionRegistry registry, out FileSessionRecordStore store);
        await store.SaveAsync(NewRecord("orphan", [0]), ct);

        await reconciler.ReconcileAsync(ct);

        Assert.False(File.Exists(Path.Combine(dir.Path, "orphan.json")));
        Assert.False(registry.TryGet("orphan", out _));
    }

    [Fact]
    public async Task Reconcile_LiveSession_ReadoptsAndKeepsRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FakeSlots slots = new();
        slots.Seed(0, "Running", isTemplate: true);
        int pid = slots.Scan()[0].ProcessId!.Value;
        int videoPort = 47989 + 9;   // slot 0 base 47989, video offset +9

        SessionReconciler reconciler = BuildReconciler(slots, new FakeUdpEndpointProbe(pid, videoPort), dir.Path, out SessionRegistry registry, out FileSessionRecordStore store);
        await store.SaveAsync(NewRecord("live", [0]), ct);

        await reconciler.ReconcileAsync(ct);

        registry.TryGet("live", out Session? session);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Connected, session.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "live.json")));
    }

    private static SessionReconciler BuildReconciler(
        FakeSlots slots, IUdpEndpointProbe probe, string recordDir, out SessionRegistry registry, out FileSessionRecordStore store)
    {
        AgentConfig config = new()
        {
            Sessions = new SessionsConfig { RecordDir = recordDir },
            Hooks = []
        };
        registry = new SessionRegistry();
        store = new FileSessionRecordStore(config, NullLogger<FileSessionRecordStore>.Instance);
        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        HookDispatcher dispatcher = new(new ProcessHookRunner(), NullLogger<HookDispatcher>.Instance);
        SessionOrchestrator orchestrator = new(config, registry, store, slots, slots, slots, loader, dispatcher, NullLogger<SessionOrchestrator>.Instance);
        return new SessionReconciler(store, slots, probe, new ApolloStreamingPortMap(), orchestrator, NullLogger<SessionReconciler>.Instance);
    }

    private static SessionRecord NewRecord(string id, int[] slotIds)
    {
        return new SessionRecord
        {
            SessionId = id,
            ClientIp = "10.0.0.1",
            SlotIds = slotIds,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
