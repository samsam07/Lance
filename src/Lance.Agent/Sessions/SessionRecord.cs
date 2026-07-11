namespace Lance.Agent.Sessions;

// Durable crash-recovery snapshot, persisted BEFORE session_started hooks run and
// deleted AFTER session_ended hooks complete. Its presence at agent startup means
// teardown never ran (see ARCHITECTURE "Sessions & tool orchestration → crash
// recovery"). Holds everything needed to replay teardown once the client is gone.
internal sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public required string ClientIp { get; init; }
    public required int[] SlotIds { get; init; }
    public ResolvedCommand[] TeardownCommands { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}

// A session_ended hook command, fully resolved (`${VAR}` substituted) at session
// start so it can be replayed at crash recovery without re-reading the hook file
// (which may have changed) or recomputing env that is gone with the client. Mirrors
// the hook command schema in SPEC; populated by the hook engine (Slice 6.3/6.4).
internal sealed record ResolvedCommand
{
    public required string Command { get; init; }
    public string[] Args { get; init; } = [];
    public string? WorkingDir { get; init; }
    public bool Async { get; init; }
    public string OnError { get; init; } = "terminate";
    public int TimeoutSeconds { get; init; } = 30;
}
