using Lance.Client.Infrastructure;
using Xunit;

namespace Lance.Client.Tests;

public sealed class BitrateModeTests
{
    [Theory]
    [InlineData("high", 0.16)]
    [InlineData("balanced", 0.10)]
    [InlineData("conservative", 0.06)]
    [InlineData("HIGH", 0.16)]
    [InlineData("  Balanced  ", 0.10)]
    public void Parse_NamedTier_MapsToBitsPerPixel(string value, double expected)
    {
        BitrateModeResult result = BitrateModes.Parse(value, sourceLayer: 1);

        Assert.True(result.IsSuccess);
        Assert.False(result.Selection.IsManual);
        Assert.Equal(expected, result.Selection.BitsPerPixel);
    }

    [Fact]
    public void Parse_Unset_DefaultsToBalanced()
    {
        BitrateModeResult result = BitrateModes.Parse(null, sourceLayer: 0);

        Assert.True(result.IsSuccess);
        Assert.False(result.Selection.IsManual);
        Assert.Equal(BitrateModes.BalancedBitsPerPixel, result.Selection.BitsPerPixel);
    }

    [Fact]
    public void Parse_Manual_TurnsDerivationOff()
    {
        BitrateModeResult result = BitrateModes.Parse("manual", sourceLayer: 1);

        Assert.True(result.IsSuccess);
        Assert.True(result.Selection.IsManual);
    }

    [Fact]
    public void Parse_Number_IsTakenAsBitsPerPixel()
    {
        BitrateModeResult result = BitrateModes.Parse("0.08", sourceLayer: 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.08, result.Selection.BitsPerPixel);
        Assert.Equal(3, result.Selection.SourceLayer);
    }

    [Theory]
    [InlineData("20000")]   // the footgun: a kbps value
    [InlineData("80")]
    [InlineData("0.001")]
    [InlineData("0")]
    public void Parse_NumberOutOfRange_Fails(string value)
    {
        BitrateModeResult result = BitrateModes.Parse(value, sourceLayer: 3);

        Assert.False(result.IsSuccess);
        Assert.Contains("bits per pixel", result.ErrorMessage);
    }

    [Fact]
    public void Parse_Nonsense_Fails()
    {
        BitrateModeResult result = BitrateModes.Parse("fastest", sourceLayer: 3);

        Assert.False(result.IsSuccess);
        Assert.Contains("not a bitrate mode", result.ErrorMessage);
    }

    [Fact]
    public void Parse_SourceLayerIsCarried()
    {
        BitrateModeResult result = BitrateModes.Parse("high", sourceLayer: 1);

        Assert.Equal(1, result.Selection.SourceLayer);
    }
}
