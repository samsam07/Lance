using Lance.Client.Commands;
using Lance.Shared.Dtos;
using Xunit;

namespace Lance.Client.Tests;

// Phase 3: add PhaseA (slot start / reuse) and PhaseB (Moonlight launch gate) flow tests
// using Option B interface extraction (IAgentClient, IProcessOperations).

public sealed class ConnectCommandTests
{
    // — ComputeAvailableCapacity ——————————————————————————————

    [Fact]
    public void ComputeAvailableCapacity_EmptyPool_ReturnsMaxSlots()
    {
        int result = ConnectCommand.ComputeAvailableCapacity([], maxSlots: 8);

        Assert.Equal(8, result);
    }

    [Fact]
    public void ComputeAvailableCapacity_AllConnected_ReturnsAllocatableOnly()
    {
        SlotDto[] slots = [MakeSlot(0, "Connected"), MakeSlot(1, "Connected"), MakeSlot(2, "Connected")];

        int result = ConnectCommand.ComputeAvailableCapacity(slots, maxSlots: 8);

        Assert.Equal(5, result); // free=0, allocatable=8-3=5
    }

    [Fact]
    public void ComputeAvailableCapacity_MixedStatuses_AllocatedAndRunningCountAsFree()
    {
        SlotDto[] slots = [MakeSlot(0, "Allocated"), MakeSlot(1, "Running"), MakeSlot(2, "Connected")];

        int result = ConnectCommand.ComputeAvailableCapacity(slots, maxSlots: 8);

        Assert.Equal(7, result); // free=2, allocatable=8-3=5
    }

    [Fact]
    public void ComputeAvailableCapacity_FullPoolAllConnected_ReturnsZero()
    {
        List<SlotDto> slots = [];
        for (int i = 0; i < 8; i++)
        {
            slots.Add(MakeSlot(i, "Connected"));
        }

        int result = ConnectCommand.ComputeAvailableCapacity(slots, maxSlots: 8);

        Assert.Equal(0, result);
    }

    [Fact]
    public void ComputeAvailableCapacity_FullPoolAllAllocated_ReturnsPoolSize()
    {
        List<SlotDto> slots = [];
        for (int i = 0; i < 8; i++)
        {
            slots.Add(MakeSlot(i, "Allocated"));
        }

        int result = ConnectCommand.ComputeAvailableCapacity(slots, maxSlots: 8);

        Assert.Equal(8, result); // free=8, allocatable=0
    }

    // — FindMoonlightFor ——————————————————————————————————————

    [Fact]
    public void FindMoonlightFor_MatchingHostPort_ReturnsPid()
    {
        List<(int Pid, string CommandLine)> moonlights =
            [(42, "moonlight stream 192.168.1.1:47989 Desktop --fps 60")];

        int pid = ConnectCommand.FindMoonlightFor(moonlights, "192.168.1.1:47989");

        Assert.Equal(42, pid);
    }

    [Fact]
    public void FindMoonlightFor_NoMatch_ReturnsZero()
    {
        List<(int Pid, string CommandLine)> moonlights =
            [(42, "moonlight stream 192.168.1.1:47989 Desktop")];

        int pid = ConnectCommand.FindMoonlightFor(moonlights, "192.168.1.1:46989");

        Assert.Equal(0, pid);
    }

    [Fact]
    public void FindMoonlightFor_CaseInsensitive_Matches()
    {
        List<(int Pid, string CommandLine)> moonlights =
            [(7, "moonlight stream MYHOST:47989 Desktop")];

        int pid = ConnectCommand.FindMoonlightFor(moonlights, "myhost:47989");

        Assert.Equal(7, pid);
    }

    // — Helpers ———————————————————————————————————————————————

    private static SlotDto MakeSlot(int id, string status)
    {
        return new SlotDto
        {
            Id = id,
            Name = $"Lance-{id}",
            Host = "localhost",
            Port = 47989 - id * 1000,
            Status = status,
            ConfigPath = string.Empty,
            ConfigName = string.Empty,
            AllocatedAt = DateTimeOffset.UtcNow
        };
    }
}
