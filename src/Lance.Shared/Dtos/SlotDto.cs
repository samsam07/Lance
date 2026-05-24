namespace Lance.Shared.Dtos;

public sealed record SlotDto
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Status { get; init; }
    public required string ConfigPath { get; init; }
    public required string ConfigName { get; init; }
    public bool IsTemplate { get; init; }
    public bool IsAdopted { get; init; }
    public required DateTimeOffset AllocatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public int? ProcessId { get; init; }
}
