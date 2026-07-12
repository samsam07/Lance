using Lance.Hooks;

namespace Lance.Agent.Sessions;

// Durable crash-recovery snapshot, persisted BEFORE session_started hooks run and
// deleted AFTER session_ended hooks complete. Its presence at agent startup means
// teardown never ran (see ARCHITECTURE "Sessions & tool orchestration → crash
// recovery"). Holds everything needed to replay teardown once the client is gone.
// TeardownCommands are ResolvedCommand from the hook engine (Lance.Hooks).
internal sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public required string ClientIp { get; init; }
    public required int[] SlotIds { get; init; }
    public ResolvedCommand[] TeardownCommands { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}
