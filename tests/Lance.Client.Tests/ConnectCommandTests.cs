using Lance.Client.Infrastructure;
using Xunit;

namespace Lance.Client.Tests;

public sealed class ConnectCommandTests
{
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
}
