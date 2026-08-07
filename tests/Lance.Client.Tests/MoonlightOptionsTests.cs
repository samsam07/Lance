using Lance.Client.Infrastructure;
using Xunit;

namespace Lance.Client.Tests;

public sealed class MoonlightOptionsTests
{
    private static MonitorInfo Monitor(
        int id = 1, int width = 1920, int height = 1080, int refreshRate = 0, string friendlyName = "")
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
            RefreshRate = refreshRate,
            FriendlyName = friendlyName
        };
    }

    private static readonly IReadOnlyList<MonitorInfo> NoMonitors = [];

    private static readonly IReadOnlyList<MonitorInfo> NamedMonitors =
    [
        Monitor(id: 1, friendlyName: "GW2480"),
        Monitor(id: 2, width: 2560, height: 1440, friendlyName: "Optix MAG27CQ"),
        Monitor(id: 3, width: 3840, height: 2160, friendlyName: "U28E590")
    ];

    private static OptionLayers Layers(string[] defaults, string[] monitor, string[] cli, string[] cliMonitor)
    {
        return new OptionLayers
        {
            DefaultOptions = defaults,
            MonitorOptions = monitor,
            CliOptions = cli,
            CliMonitorOptions = cliMonitor
        };
    }

    private static OptionLayers NoLayers()
    {
        return Layers([], [], [], []);
    }

    // Derivation off, so layer-order tests read without bitrate noise.
    private static BitrateSelection Manual => new() { IsManual = true, Name = BitrateModes.Manual };

    private static BitrateSelection Auto(double bitsPerPixel, int sourceLayer)
    {
        return new BitrateSelection { BitsPerPixel = bitsPerPixel, Name = "test", SourceLayer = sourceLayer };
    }

    // — Build: layer order ————————————————————————————————————

    [Fact]
    public void Build_EmitsLayersInPrecedenceOrder()
    {
        string[] options = MoonlightOptions.Build(
            Monitor(width: 3840, height: 2160), 1,
            Layers(["--video-codec", "HEVC"], ["--bitrate", "40000"], ["--fps", "30"], ["--bitrate", "50000"]),
            Manual);

        Assert.Equal(
            ["--resolution", "3840x2160", "--video-codec", "HEVC", "--bitrate", "40000", "--fps", "30", "--bitrate", "50000"],
            options);
    }

    [Fact]
    public void Build_GeneratedResolutionIsFirst_SoAnyLayerCanOverrideIt()
    {
        // Streaming a 4K panel at 1440p is the headline use case: the generated
        // resolution must be overridable by a later layer.
        string[] options = MoonlightOptions.Build(
            Monitor(width: 3840, height: 2160), 1,
            Layers([], ["--resolution", "2560x1440"], [], []),
            Manual);

        Assert.Equal(["--resolution", "3840x2160", "--resolution", "2560x1440"], options);
    }

    [Fact]
    public void Build_NoMonitor_OmitsGeneratedResolution()
    {
        string[] options = MoonlightOptions.Build(
            null, 1,
            Layers(["--video-codec", "HEVC"], [], [], []),
            Manual);

        Assert.Equal(["--video-codec", "HEVC"], options);
    }

    [Fact]
    public void Build_CliMonitorOptionsComeLast()
    {
        string[] options = MoonlightOptions.Build(
            null, 1,
            Layers([], [], ["--bitrate", "20000"], ["--bitrate", "10000"]),
            Manual);

        Assert.Equal(["--bitrate", "20000", "--bitrate", "10000"], options);
    }

    // — Build: generated --fps ————————————————————————————————

    [Fact]
    public void Build_KnownRefreshRate_GeneratesFps()
    {
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 60), 1,
            Layers([], [], [], []),
            Manual);

        Assert.Equal(["--resolution", "1920x1080", "--fps", "60"], options);
    }

    [Fact]
    public void Build_HighRefreshRate_IsCappedAt60()
    {
        // A 144 Hz panel must not multiply the agent's encoder and uplink load.
        string[] options = MoonlightOptions.Build(
            Monitor(width: 2560, height: 1440, refreshRate: 144), 1,
            Layers([], [], [], []),
            Manual);

        Assert.Equal(["--resolution", "2560x1440", "--fps", "60"], options);
    }

    [Fact]
    public void Build_FractionalRateReportedAsWholeNumber_IsPassedThrough()
    {
        // A 59.94 Hz panel reports 59; that is closer to the truth than rounding to 60.
        string[] options = MoonlightOptions.Build(
            Monitor(width: 3840, height: 2160, refreshRate: 59), 1,
            Layers([], [], [], []),
            Manual);

        Assert.Equal(["--resolution", "3840x2160", "--fps", "59"], options);
    }

    [Theory]
    [InlineData(0)]   // Windows: "hardware default"
    [InlineData(1)]   // Windows: "hardware default"
    public void Build_UnknownRefreshRate_OmitsFps(int refreshRate)
    {
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: refreshRate), 1,
            Layers([], [], [], []),
            Manual);

        Assert.Equal(["--resolution", "1920x1080"], options);
    }

    [Fact]
    public void Build_GeneratedFpsIsFirst_SoAnyLayerCanOverrideIt()
    {
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 144), 1,
            Layers([], ["--fps", "120"], [], []),
            Manual);

        Assert.Equal(["--resolution", "1920x1080", "--fps", "60", "--fps", "120"], options);
    }

    // — Build: derived bitrate ————————————————————————————————

    [Theory]
    // The tier table in STREAM_TUNING_SPEC §4.1, checked against the real formula.
    [InlineData(1920, 1080, 0.16, 20000)]
    [InlineData(2560, 1440, 0.16, 35000)]
    [InlineData(3840, 2160, 0.16, 80000)]
    [InlineData(1920, 1080, 0.10, 12000)]
    [InlineData(2560, 1440, 0.10, 22000)]
    [InlineData(3840, 2160, 0.10, 50000)]
    [InlineData(1920, 1080, 0.06, 7000)]
    [InlineData(2560, 1440, 0.06, 13000)]
    [InlineData(3840, 2160, 0.06, 30000)]
    public void Build_DerivesBitrateFromPixelRate(int width, int height, double bitsPerPixel, int expectedKbps)
    {
        string[] options = MoonlightOptions.Build(
            Monitor(width: width, height: height, refreshRate: 60), 1, NoLayers(), Auto(bitsPerPixel, sourceLayer: 0));

        Assert.Equal(["--bitrate", expectedKbps.ToString()], options[^2..]);
    }

    [Fact]
    public void Build_DerivesFromStreamedResolution_NotThePanelsNative()
    {
        // Overriding a 4K panel down to 1440p must also shrink its bitrate, otherwise
        // the saving is only half realised.
        string[] options = MoonlightOptions.Build(
            Monitor(width: 3840, height: 2160, refreshRate: 60), 1,
            Layers([], ["--resolution", "2560x1440"], [], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 0));

        Assert.Equal(["--bitrate", "22000"], options[^2..]);
    }

    [Fact]
    public void Build_ManualMode_DerivesNothing()
    {
        string[] options = MoonlightOptions.Build(Monitor(refreshRate: 60), 1, NoLayers(), Manual);

        Assert.DoesNotContain("--bitrate", options);
    }

    [Fact]
    public void Build_NoModeSetAndExplicitBitrate_LeavesTheExplicitValueAlone()
    {
        // §4.2 row 2 — the inferred-manual case that keeps existing configs working.
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 60), 1,
            Layers(["--bitrate", "80000"], [], [], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 0));

        Assert.Equal(["--resolution", "1920x1080", "--fps", "60", "--bitrate", "80000"], options);
    }

    [Fact]
    public void Build_ConfigModeVersusExplicitBitrate_ExplicitWins()
    {
        // §4.2 row 4 — a config-level mode does not outrank an explicit value.
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 60), 1,
            Layers(["--bitrate", "80000"], [], [], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 1));

        Assert.Equal(["--bitrate", "80000"], options[^2..]);
    }

    [Fact]
    public void Build_CliModeVersusConfigBitrate_ModeWins()
    {
        // §4.2 row 6 — the command line outranks the config file.
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 60), 1,
            Layers(["--bitrate", "80000"], [], [], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 3));

        Assert.Equal(["--bitrate", "12000"], options[^2..]);
    }

    [Fact]
    public void Build_CliModeVersusCliBitrate_ExplicitWins()
    {
        // §4.2 row 7 — within the command line, the explicit value is the more
        // specific intent.
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 60), 1,
            Layers([], [], ["--bitrate", "30000"], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 3));

        Assert.Equal(["--bitrate", "30000"], options[^2..]);
    }

    [Fact]
    public void Build_DerivationCapsFpsAt60_EvenWhenTheStreamRunsFaster()
    {
        // §6 ceiling 2: the stream still runs at 120, but the budget is 60fps-worth.
        string[] options = MoonlightOptions.Build(
            Monitor(refreshRate: 144), 1,
            Layers(["--fps", "120"], [], [], []),
            Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 0));

        Assert.Equal(["--bitrate", "12000"], options[^2..]);
    }

    [Fact]
    public void Build_NoResolutionKnown_LeavesBitrateToMoonlight()
    {
        string[] options = MoonlightOptions.Build(null, 1, NoLayers(), Auto(BitrateModes.BalancedBitsPerPixel, sourceLayer: 0));

        Assert.DoesNotContain("--bitrate", options);
    }

    // — Config entries ————————————————————————————————————————

    [Fact]
    public void ParseConfigEntries_NumericKeys_ResolveToMonitorIds()
    {
        Dictionary<string, string[]> entries = new()
        {
            ["1"] = ["--bitrate", "10000"],
            ["3"] = ["--bitrate", "40000"]
        };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000"], result.ByMonitorId[1]);
        Assert.Equal(["--bitrate", "40000"], result.ByMonitorId[3]);
    }

    [Fact]
    public void ParseConfigEntries_Null_ReturnsEmpty()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(null, NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.ByMonitorId);
    }

    // — Keys by monitor name ——————————————————————————————————

    [Theory]
    [InlineData("GW2480", 1)]
    [InlineData("Optix MAG27CQ", 2)]
    [InlineData("gw2480", 1)]              // case-insensitive
    [InlineData("U28E590", 3)]
    public void ParseConfigEntries_NameKey_ResolvesToThatMonitorsId(string key, int expectedId)
    {
        Dictionary<string, string[]> entries = new() { [key] = ["--bitrate", "10000"] };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000"], result.ByMonitorId[expectedId]);
    }

    [Fact]
    public void ParseConfigEntries_DeviceNameKey_AlsoResolves()
    {
        Dictionary<string, string[]> entries = new() { [@"\\.\DISPLAY2"] = ["--bitrate", "20000"] };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "20000"], result.ByMonitorId[2]);
    }

    [Fact]
    public void ParseConfigEntries_PartialName_DoesNotMatch()
    {
        // Exact match only — "MAG27" matching "Optix MAG27CQ" would be silently
        // ambiguous across similar models.
        Dictionary<string, string[]> entries = new() { ["MAG27"] = ["--bitrate", "20000"] };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.ByMonitorId);
    }

    [Fact]
    public void ParseConfigEntries_UnknownName_IsSkippedNotFatal()
    {
        Dictionary<string, string[]> entries = new()
        {
            ["GW2480"] = ["--bitrate", "10000"],
            ["NoSuchPanel"] = ["--bitrate", "99000"]
        };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000"], result.ByMonitorId[1]);
        Assert.Single(result.ByMonitorId);
    }

    [Fact]
    public void ParseConfigEntries_NameMatchingTwoIdenticalPanels_Fails()
    {
        IReadOnlyList<MonitorInfo> twins =
        [
            Monitor(id: 1, friendlyName: "GW2480"),
            Monitor(id: 2, friendlyName: "GW2480")
        ];
        Dictionary<string, string[]> entries = new() { ["GW2480"] = ["--bitrate", "10000"] };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, twins);

        Assert.False(result.IsSuccess);
        Assert.Contains("more than one monitor", result.ErrorMessage);
    }

    [Fact]
    public void ParseCliEntries_NameKey_Resolves()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(["Optix MAG27CQ=--bitrate 22000"], NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "22000"], result.ByMonitorId[2]);
    }

    [Fact]
    public void ParseCliEntries_NameAndIdForSameMonitor_Appends()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(
            ["GW2480=--bitrate 10000", "1=--fps 30"], NamedMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000", "--fps", "30"], result.ByMonitorId[1]);
    }

    [Fact]
    public void ParseConfigEntries_TwoKeysSameMonitor_Fails()
    {
        // "1" and "01" both mean monitor 1 — ambiguous, so refuse rather than pick one.
        Dictionary<string, string[]> entries = new()
        {
            ["1"] = ["--bitrate", "10000"],
            ["01"] = ["--bitrate", "20000"]
        };

        MonitorOptionsResult result = MoonlightOptions.ParseConfigEntries(entries, NoMonitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("more than once", result.ErrorMessage);
    }

    // — CLI entries ————————————————————————————————————————————

    [Fact]
    public void ParseCliEntries_SplitsIdFromOptions()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(["1=--bitrate 10000"], NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000"], result.ByMonitorId[1]);
    }

    [Fact]
    public void ParseCliEntries_MultipleOptionsForOneMonitor_AreSplitOnWhitespace()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(["3=--bitrate 40000 --fps 30"], NoMonitors);

        Assert.Equal(["--bitrate", "40000", "--fps", "30"], result.ByMonitorId[3]);
    }

    [Fact]
    public void ParseCliEntries_RepeatedForSameMonitor_Appends()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(["1=--bitrate 10000", "1=--fps 30"], NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Equal(["--bitrate", "10000", "--fps", "30"], result.ByMonitorId[1]);
    }

    [Fact]
    public void ParseCliEntries_Empty_ReturnsEmpty()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries([], NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.ByMonitorId);
    }

    [Theory]
    [InlineData("1--bitrate 10000")]   // no separator
    [InlineData("=--bitrate 10000")]   // no monitor
    public void ParseCliEntries_Malformed_Fails(string entry)
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries([entry], NoMonitors);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed", result.ErrorMessage);
    }

    [Fact]
    public void ParseCliEntries_NameWithNoMonitorsKnown_IsSkipped()
    {
        MonitorOptionsResult result = MoonlightOptions.ParseCliEntries(["GW2480=--bitrate 10000"], NoMonitors);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.ByMonitorId);
    }
}
