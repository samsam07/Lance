namespace Lance.Shared.Dtos;

// Connect handshake (client → agent POST /sessions). The client mints the id and
// requests one slot per monitor; the agent returns the running slots to stream to.
public sealed record CreateSessionRequest
{
    public required string SessionId { get; init; }
    public required int Count { get; init; }
}

public sealed record SessionResponse
{
    public required string SessionId { get; init; }
    public required SlotDto[] Slots { get; init; }
}

public sealed record SessionsListResponse
{
    public required SessionResponse[] Sessions { get; init; }
}
