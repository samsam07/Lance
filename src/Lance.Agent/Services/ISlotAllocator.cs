using Lance.Shared.Dtos;

namespace Lance.Agent.Services;

internal sealed record AllocateResult
{
    public IReadOnlyList<SlotDto> Slots { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorCode is null;
}

internal sealed record DeallocateResult
{
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int HttpStatus { get; init; } = 200;
    public bool IsSuccess => ErrorCode is null;
}

internal interface ISlotAllocator
{
    AllocateResult Allocate(int count);
    DeallocateResult Deallocate(int id);
}
