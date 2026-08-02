// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// Sizing constants and the small pieces of arithmetic shared by the Theta
/// update sketches.
/// </summary>
internal static class ThetaLimits
{
    /// <summary>Smallest allowed <c>lg(k)</c>: 16 nominal entries.</summary>
    public const int MinLgNominalEntries = 4;

    /// <summary>Largest allowed <c>lg(k)</c>: 67,108,864 nominal entries.</summary>
    public const int MaxLgNominalEntries = 26;

    /// <summary>Smallest hash table the sketch will allocate, as <c>lg(length)</c>.</summary>
    public const int MinLgArrLongs = 5;

    /// <summary>Default <c>k</c>. Gives roughly ±1.6% relative error at 68% confidence.</summary>
    public const int DefaultNominalEntries = 4096;

    /// <summary>Load factor at which a table below full size grows. Tuned for update speed.</summary>
    public const double ResizeThreshold = 0.5;

    /// <summary>Load factor at which a full-size table must instead drop theta and sweep.</summary>
    public const double RebuildThreshold = 15.0 / 16.0;

    /// <summary>
    /// The starting hash table size for a sketch that will grow to
    /// <paramref name="lgTarget"/> by repeated multiplication by the resize
    /// factor. Picking a sub-multiple means the final resize lands exactly on
    /// the target instead of overshooting it.
    /// </summary>
    public static int StartingSubMultiple(int lgTarget, int lgResizeFactor, int lgMin) =>
        lgTarget <= lgMin ? lgMin
        : lgResizeFactor == 0 ? lgTarget
        : ((lgTarget - lgMin) % lgResizeFactor) + lgMin;

    /// <summary>
    /// Validates a nominal entry count and returns its <c>lg</c>, rounding up to
    /// the next power of two.
    /// </summary>
    public static int CheckNominalEntries(int nominalEntries)
    {
        if (nominalEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nominalEntries), nominalEntries, "Nominal entries must be positive.");
        }

        int lg = LgCeilingPowerOf2(nominalEntries);
        if (lg < MinLgNominalEntries || lg > MaxLgNominalEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nominalEntries),
                nominalEntries,
                $"Nominal entries must be between {1 << MinLgNominalEntries} and {1 << MaxLgNominalEntries}.");
        }
        return lg;
    }

    /// <summary>Base-2 logarithm of the smallest power of two at least as large as <paramref name="value"/>.</summary>
    public static int LgCeilingPowerOf2(int value)
    {
        int lg = 0;
        while ((1 << lg) < value)
        {
            lg++;
        }
        return lg;
    }

    /// <summary>
    /// Converts a sampling probability to its theta representation.
    /// </summary>
    /// <remarks>
    /// The saturation matters: <c>p = 1.0</c> gives a product that rounds up to
    /// 2^63 as a double, which is one past <see cref="long.MaxValue"/>. Java's
    /// narrowing conversion clamps, but C#'s produces an unspecified result in an
    /// unchecked context — on x64 that is <see cref="long.MinValue"/>, which
    /// would make every sketch reject every value. Clamp explicitly.
    /// </remarks>
    public static long ThetaFromSamplingProbability(float p)
    {
        double theta = p * (double)long.MaxValue;
        return theta >= long.MaxValue ? long.MaxValue : (long)theta;
    }

    /// <summary>
    /// The retained-entry count above which the table must grow or be swept.
    /// Below full size the table grows at half full; at full size it runs to
    /// 15/16 before dropping theta, because growing is no longer an option.
    /// </summary>
    public static int HashTableThreshold(int lgNominalEntries, int lgArrLongs)
    {
        double fraction = lgArrLongs <= lgNominalEntries ? ResizeThreshold : RebuildThreshold;
        return (int)(fraction * (1 << lgArrLongs));
    }
}
