using Lance.Shared.Dtos;

namespace Lance.Agent.Services;

internal interface ISlotScanner
{
    IReadOnlyList<SlotDto> Scan();
}
