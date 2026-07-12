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
    Task EndSessionAsync(string sessionId, string source, CancellationToken cancellationToken = default);
}
