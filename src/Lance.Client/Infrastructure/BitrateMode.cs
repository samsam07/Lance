using System.Globalization;

namespace Lance.Client.Infrastructure;

// How a stream's bitrate is chosen, plus the option layer the choice came from.
// SourceLayer decides who wins when a stream also carries an explicit --bitrate:
// an explicit value at the same layer or higher beats the derivation
// (docs/STREAM_TUNING_SPEC.md §4.2).
internal sealed record BitrateSelection
{
    public bool IsManual { get; init; }
    public double BitsPerPixel { get; init; }
    public string Name { get; init; } = BitrateModes.Balanced;
    public int SourceLayer { get; init; }
}

internal sealed record BitrateModeResult
{
    public BitrateSelection Selection { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorMessage is null;
}

// Parses the `bitrateMode` config field / `--bitrate-mode` flag. A named tier maps to
// a bits-per-pixel figure; a bare number supplies one directly; `manual` turns the
// derivation off and leaves the bitrate to whatever the options say.
internal static class BitrateModes
{
    public const string High = "high";
    public const string Balanced = "balanced";
    public const string Conservative = "conservative";
    public const string Manual = "manual";

    // Moonlight's own defaults sit at ~0.16 bits/pixel (1080p60 -> 20 Mbps and
    // 4K60 -> 80 Mbps both land there). Those are gaming figures with deliberate
    // headroom; a desktop compresses far better, so the default tier is lower.
    public const double HighBitsPerPixel = 0.16;
    public const double BalancedBitsPerPixel = 0.10;
    public const double ConservativeBitsPerPixel = 0.06;

    // A caller-supplied number is bits per pixel, never kbps. Rejecting anything
    // outside this range is what stops `--bitrate-mode 20000` ("20 Mbps") from being
    // read as 20000 bits per pixel.
    public const double MinBitsPerPixel = 0.01;
    public const double MaxBitsPerPixel = 1.0;

    public static BitrateModeResult Parse(string? value, int sourceLayer)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new BitrateModeResult
            {
                Selection = new BitrateSelection { BitsPerPixel = BalancedBitsPerPixel, Name = Balanced, SourceLayer = sourceLayer }
            };
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized == Manual)
        {
            return new BitrateModeResult
            {
                Selection = new BitrateSelection { IsManual = true, Name = Manual, SourceLayer = sourceLayer }
            };
        }

        double? tier = normalized switch
        {
            High => HighBitsPerPixel,
            Balanced => BalancedBitsPerPixel,
            Conservative => ConservativeBitsPerPixel,
            _ => null
        };

        if (tier is double bitsPerPixel)
        {
            return new BitrateModeResult
            {
                Selection = new BitrateSelection { BitsPerPixel = bitsPerPixel, Name = normalized, SourceLayer = sourceLayer }
            };
        }

        return ParseNumeric(normalized, sourceLayer);
    }

    private static BitrateModeResult ParseNumeric(string normalized, int sourceLayer)
    {
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double supplied))
        {
            return new BitrateModeResult
            {
                ErrorMessage = $"'{normalized}' is not a bitrate mode. Use {High}, {Balanced}, {Conservative}, {Manual}, or a bits-per-pixel number such as 0.10."
            };
        }

        if (supplied < MinBitsPerPixel || supplied > MaxBitsPerPixel)
        {
            return new BitrateModeResult
            {
                ErrorMessage = $"Bitrate mode '{normalized}' is out of range. Expected bits per pixel between {MinBitsPerPixel} and {MaxBitsPerPixel} (for example 0.10) — not a kbps value."
            };
        }

        return new BitrateModeResult
        {
            Selection = new BitrateSelection
            {
                BitsPerPixel = supplied,
                Name = supplied.ToString(CultureInfo.InvariantCulture),
                SourceLayer = sourceLayer
            }
        };
    }
}
