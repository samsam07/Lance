using Lance.Client.Commands;
using Lance.Client.Infrastructure;
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

    // — FindMoonlightsForSlot ————————————————————————————————

    [Fact]
    public void FindMoonlightsForSlot_MatchingHostPort_ReturnsPid()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
            [(42, "moonlight stream 192.168.1.1:47989 Desktop --fps 60", null)];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "192.168.1.1:47989", "Lance-0");

        Assert.Equal([42], pids);
    }

    [Fact]
    public void FindMoonlightsForSlot_NoMatch_ReturnsEmpty()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
            [(42, "moonlight stream 192.168.1.1:47989 Desktop", null)];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "192.168.1.1:46989", "Lance-1");

        Assert.Empty(pids);
    }

    [Fact]
    public void FindMoonlightsForSlot_HostPortCaseInsensitive_Matches()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
            [(7, "moonlight stream MYHOST:47989 Desktop", null)];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "myhost:47989", "Lance-0");

        Assert.Equal([7], pids);
    }

    [Fact]
    public void FindMoonlightsForSlot_WindowTitleMatch_ReturnsPid()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
            [(99, "moonlight", "Lance-1 - Moonlight")];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "host:46989", "Lance-1");

        Assert.Equal([99], pids);
    }

    [Fact]
    public void FindMoonlightsForSlot_WindowTitleNoFalsePositive_DoesNotMatchLongerName()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
            [(99, "moonlight", "Lance-10 - Moonlight")];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "host:46989", "Lance-1");

        Assert.Empty(pids);
    }

    [Fact]
    public void FindMoonlightsForSlot_MultipleMatchesSameSlot_ReturnsAll()
    {
        List<(int Pid, string CommandLine, string? WindowTitle)> moonlights =
        [
            (10, "moonlight stream host:47989 Desktop", null),
            (11, "moonlight", "Lance-0 - Moonlight"),
        ];

        IReadOnlyList<int> pids = ProcessCommandLine.FindMoonlightsForSlot(moonlights, "host:47989", "Lance-0");

        Assert.Equal([10, 11], pids);
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
