namespace Lance.Agent.Sessions;

// One live session held in memory — a `lance connect`, the slots it acquired, and
// its current lifecycle state. The durable crash-recovery snapshot that outlives the
// process is SessionRecord; this is the runtime view.
internal sealed record Session
{
    public required string Id { get; init; }
    public required string ClientIp { get; init; }
    public required IReadOnlyList<int> SlotIds { get; init; }
    public required SessionState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
}
