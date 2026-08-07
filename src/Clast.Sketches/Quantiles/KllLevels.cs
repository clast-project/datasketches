// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// The level arithmetic of the KLL sketch: how big each level is allowed to
/// get, and how much the whole structure holds.
/// </summary>
/// <remarks>
/// <para>
/// A KLL sketch is a stack of sorted buffers. Level 0 holds the raw stream;
/// each level above holds items of twice the weight of the one below. The
/// capacities shrink geometrically going down the stack — level capacity is
/// about <c>k * (2/3)^depth</c>, floored at <c>m</c> — and that ratio is what
/// makes the total space <c>O(k)</c> while the represented stream is
/// unbounded.
/// </para>
/// <para>
/// None of this depends on the element type, so it is shared by every KLL
/// instantiation.
/// </para>
/// </remarks>
internal static class KllLevels
{
    /// <summary>The default <c>k</c>, giving about 1.33% single-sided rank error at 99% confidence.</summary>
    public const int DefaultK = 200;

    /// <summary>The largest <c>k</c>; it is serialized as an unsigned 16-bit field.</summary>
    public const int MaxK = (1 << 16) - 1;

    /// <summary>The default minimum level width in items.</summary>
    public const int DefaultM = 8;

    /// <summary>The smallest permitted <c>m</c>.</summary>
    public const int MinM = 2;

    /// <summary>The largest permitted <c>m</c>.</summary>
    public const int MaxM = 8;

    // Fitted constants for the empirically measured 99th-percentile rank error.
    private const double EpsDeltaThreshold = 1E-6;
    private const double MinEps = 4.7634E-5;
    private const double PmfCoefficient = 2.446;
    private const double PmfExponent = 0.9433;
    private const double CdfCoefficient = 2.296;
    private const double CdfExponent = 0.9723;

    /// <summary>Exact powers of three, indexed by exponent, up to 3^30.</summary>
    private static readonly long[] PowersOfThree =
    [
        1, 3, 9, 27, 81, 243, 729, 2187, 6561, 19683, 59049, 177147, 531441,
        1594323, 4782969, 14348907, 43046721, 129140163, 387420489, 1162261467,
        3486784401L, 10460353203L, 31381059609L, 94143178827L, 282429536481L,
        847288609443L, 2541865828329L, 7625597484987L, 22876792454961L, 68630377364883L,
        205891132094649L,
    ];

    public static void CheckK(int k, int m)
    {
        if (k < m || k > MaxK)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, $"k must be >= {m} and <= {MaxK}.");
        }
    }

    public static void CheckM(int m)
    {
        if (m < MinM || m > MaxM || (m & 1) == 1)
        {
            throw new ArgumentOutOfRangeException(nameof(m), m, "m must be even and between 2 and 8.");
        }
    }

    /// <summary>
    /// The item capacity of one level, given how far it sits below the top.
    /// </summary>
    public static int LevelCapacity(int k, int numLevels, int level, int m)
    {
        int depth = numLevels - level - 1;
        return (int)Math.Max(m, IntCapAux(k, depth));
    }

    /// <summary>
    /// The total capacity of a sketch whose levels are all full.
    /// </summary>
    public static int ComputeTotalItemCapacity(int k, int m, int numLevels)
    {
        long total = 0;
        for (int level = 0; level < numLevels; level++)
        {
            total += LevelCapacity(k, numLevels, level, m);
        }
        return (int)total;
    }

    /// <summary>The number of items currently held at a level.</summary>
    public static int CurrentLevelSize(int level, int numLevels, int[] levels) =>
        level >= numLevels ? 0 : levels[level + 1] - levels[level];

    /// <summary>Retained items above level zero, which is what a merge has to carry over.</summary>
    public static int NumRetainedAboveLevelZero(int numLevels, int[] levels) =>
        levels[numLevels] - levels[1];

    /// <summary>The lowest level that has reached or exceeded its capacity.</summary>
    public static int FindLevelToCompact(int k, int m, int numLevels, int[] levels)
    {
        int level = 0;
        while (true)
        {
            int pop = levels[level + 1] - levels[level];
            int cap = LevelCapacity(k, numLevels, level, m);
            if (pop >= cap) { return level; }
            level++;
        }
    }

    /// <summary>A conservative upper bound on the level count for a stream of <paramref name="n"/> items.</summary>
    public static int UbOnNumLevels(long n) => n < 1 ? 1 : 64 - Bits.LeadingZeroCount((ulong)n);

    /// <summary>
    /// The total weight the levels represent. Equals <c>n</c> for a well-formed
    /// sketch, which makes it a cheap invariant check after a merge.
    /// </summary>
    public static long SumSampleWeights(int numLevels, int[] levels)
    {
        long total = 0;
        long weight = 1;
        for (int i = 0; i < numLevels; i++)
        {
            total += weight * (levels[i + 1] - levels[i]);
            weight *= 2;
        }
        return total;
    }

    /// <summary>
    /// The normalized rank error for a given <c>k</c>. The constants are fitted
    /// to the 99th percentile of measured error over thousands of trials, not
    /// derived, so they are reproduced from the reference exactly.
    /// </summary>
    public static double NormalizedRankError(int k, bool pmf) =>
        pmf ? PmfCoefficient / Math.Pow(k, PmfExponent)
            : CdfCoefficient / Math.Pow(k, CdfExponent);

    /// <summary>The smallest <c>k</c> that achieves a requested rank error.</summary>
    public static int KFromEpsilon(double epsilon, bool pmf)
    {
        double eps = Math.Max(epsilon, MinEps);
        double kdbl = pmf
            ? Math.Exp(Math.Log(PmfCoefficient / eps) / PmfExponent)
            : Math.Exp(Math.Log(CdfCoefficient / eps) / CdfExponent);
        double krnd = Math.Round(kdbl, MidpointRounding.AwayFromZero);
        double del = Math.Abs(krnd - kdbl);
        int k = (int)(del < EpsDeltaThreshold ? krnd : Math.Ceiling(kdbl));
        return Math.Max(MinM, Math.Min(MaxK, k));
    }

    /// <summary>
    /// Level capacity as an exact integer, using the reference's folding trick
    /// past depth 30 so the intermediate <c>2k * 2^depth</c> cannot overflow.
    /// </summary>
    private static long IntCapAux(int k, int depth)
    {
        if (depth <= 30) { return IntCapAuxAux(k, depth); }
        int half = depth / 2;
        int rest = depth - half;
        long tmp = IntCapAuxAux(k, half);
        return IntCapAuxAux(tmp, rest);
    }

    /// <summary>
    /// <c>round(k * (2/3)^depth)</c> in integer arithmetic. Pre-multiplying by
    /// two keeps the fraction and lets the final shift do the rounding.
    /// </summary>
    private static long IntCapAuxAux(long k, int depth)
    {
        long twok = k << 1;
        long tmp = (twok << depth) / PowersOfThree[depth];
        return (long)((ulong)(tmp + 1L) >> 1);
    }
}
