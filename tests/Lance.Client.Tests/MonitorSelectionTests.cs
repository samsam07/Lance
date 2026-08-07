using Lance.Client.Infrastructure;
using Xunit;

namespace Lance.Client.Tests;

public sealed class MonitorSelectionTests
{
    private static MonitorInfo Monitor(int id, string friendlyName = "", int width = 1920, int height = 1080)
    {
        return new MonitorInfo
        {
            Id = id,
            Name = $@"\\.\DISPLAY{id}",
            Width = width,
            Height = height,
            X = 0,
            Y = 0,
            IsPrimary = id == 1,
            FriendlyName = friendlyName
        };
    }

    private static readonly IReadOnlyList<MonitorInfo> Monitors =
    [
        Monitor(1, "Optix MAG27CQ", 2560, 1440),
        Monitor(2, "BenQ GW2480"),
        Monitor(3, "U28E590", 3840, 2160)
    ];

    private static int[] IdsOf(MonitorSelectionResult result)
    {
        int[] ids = new int[result.Targets.Count];
        for (int i = 0; i < result.Targets.Count; i++)
        {
            ids[i] = result.Targets[i].Id;
        }

        return ids;
    }

    // — Default selection ——————————————————————————————————————

    [Fact]
    public void Resolve_NoValue_SelectsEveryMonitorInOrder()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve(null, Monitors);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 3], IdsOf(result));
    }

    [Fact]
    public void Resolve_NoValueAndNoMonitorsDetected_Fails()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve(null, []);

        Assert.False(result.IsSuccess);
        Assert.Contains("Monitor detection failed", result.ErrorMessage);
    }

    // — By id ——————————————————————————————————————————————————

    [Fact]
    public void Resolve_Ids_KeepsTheOrderGiven()
    {
        // Order is load-bearing: position i maps to slot i.
        MonitorSelectionResult result = MonitorSelection.Resolve("3,1", Monitors);

        Assert.Equal([3, 1], IdsOf(result));
    }

    [Fact]
    public void Resolve_UnknownId_IsSkipped()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,9", Monitors);

        Assert.True(result.IsSuccess);
        Assert.Equal([1], IdsOf(result));
    }

    [Fact]
    public void Resolve_IdsWhenDetectionFailed_AreStillAccepted()
    {
        // With no monitors detected the user must still be able to connect manually.
        MonitorSelectionResult result = MonitorSelection.Resolve("1,2", []);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], IdsOf(result));
        Assert.All(result.Targets, target => Assert.Null(target.Monitor));
    }

    // — By name ————————————————————————————————————————————————

    [Theory]
    [InlineData("U28E590", 3)]
    [InlineData("benq gw2480", 2)]              // case-insensitive
    [InlineData(@"\\.\DISPLAY1", 1)]            // device name also works
    public void Resolve_Name_SelectsThatMonitor(string key, int expectedId)
    {
        MonitorSelectionResult result = MonitorSelection.Resolve(key, Monitors);

        Assert.True(result.IsSuccess);
        Assert.Equal([expectedId], IdsOf(result));
    }

    [Fact]
    public void Resolve_NamesWithSpaces_SplitOnCommasOnly()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("BenQ GW2480,Optix MAG27CQ", Monitors);

        Assert.Equal([2, 1], IdsOf(result));
    }

    [Fact]
    public void Resolve_MixedIdsAndNames_Works()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,U28E590", Monitors);

        Assert.Equal([1, 3], IdsOf(result));
    }

    [Fact]
    public void Resolve_UnknownName_IsSkipped()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,NoSuchPanel", Monitors);

        Assert.True(result.IsSuccess);
        Assert.Equal([1], IdsOf(result));
    }

    [Fact]
    public void Resolve_PartialName_DoesNotMatch()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,MAG27", Monitors);

        Assert.Equal([1], IdsOf(result));
    }

    [Fact]
    public void Resolve_AmbiguousName_Fails()
    {
        IReadOnlyList<MonitorInfo> twins = [Monitor(1, "GW2480"), Monitor(2, "GW2480")];

        MonitorSelectionResult result = MonitorSelection.Resolve("GW2480", twins);

        Assert.False(result.IsSuccess);
        Assert.Contains("more than one monitor", result.ErrorMessage);
    }

    // — Duplicates ————————————————————————————————————————————

    [Fact]
    public void Resolve_SameIdTwice_Fails()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,1", Monitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("more than once", result.ErrorMessage);
    }

    [Fact]
    public void Resolve_SameMonitorByIdAndName_Fails()
    {
        // The reason duplicates must be detected after resolution, not on the raw text:
        // these two spellings are one screen and would otherwise claim two slots.
        MonitorSelectionResult result = MonitorSelection.Resolve("3,U28E590", Monitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("'3' and 'U28E590'", result.ErrorMessage);
    }

    [Fact]
    public void Resolve_SameMonitorByNameAndDeviceName_Fails()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve(@"U28E590,\\.\DISPLAY3", Monitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("more than once", result.ErrorMessage);
    }

    // — Nothing usable ————————————————————————————————————————

    [Fact]
    public void Resolve_NothingResolves_Fails()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("NoSuchPanel,AlsoMissing", Monitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("No usable monitors", result.ErrorMessage);
    }

    [Fact]
    public void Resolve_StrayCommas_AreIgnored()
    {
        MonitorSelectionResult result = MonitorSelection.Resolve("1,,3", Monitors);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 3], IdsOf(result));
    }
}
