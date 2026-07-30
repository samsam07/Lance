using Lance.Shared.Dtos;

namespace Lance.Agent.Sessions;

internal sealed record SessionCreationResult
{
    public IReadOnlyList<SlotDto> Slots { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int HttpStatus { get; init; } = 200;
    public bool IsSuccess => ErrorCode is null;
}

// Owns a session's whole life: the connect handshake (vet id, pick + start slots,
// persist the crash-recovery record, run session_started hooks) and teardown (run the
// snapshotted session_ended commands, delete the record, free the slots). The
// detection loop calls EndSessionAsync; the POST /sessions endpoint calls
// CreateSessionAsync.
internal interface ISessionOrchestrator
{
    Task<SessionCreationResult> CreateSessionAsync(string sessionId, int count, string clientIp, string agentIp, CancellationToken cancellationToken = default);

    // Ends a session: run teardown, then (unless keepRunning) stop its slots so the
    // virtual displays come down. keepRunning is the `disconnect --keep-running` opt-out
    // for a fast reconnect; automatic ends (probe-watch / provision-timeout) always stop.
    Task EndSessionAsync(string sessionId, string source, bool keepRunning = false, CancellationToken cancellationToken = default);

    // Query for the disconnect fast-path: which slots a session holds, so the client
    // can match and kill the right Moonlights.
    SessionResponse? GetSession(string sessionId);
    SessionsListResponse GetAllSessions();

    // Crash recovery (Slice 1.5). A surviving record whose session is still streaming
    // is re-adopted into memory (Connected); an orphaned one has its teardown replayed.
    void Readopt(SessionRecord record);
    Task ReplayTeardownAsync(SessionRecord record, string source, CancellationToken cancellationToken = default);
}
