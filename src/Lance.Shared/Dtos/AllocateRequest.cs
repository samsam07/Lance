namespace Lance.Shared.Dtos;

public sealed record AllocateRequest
{
    public required int Count { get; init; }
}
