// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.thetacommon.BinomialBoundsN from
// apache/datasketches-java. See NOTICE.

namespace Clast.Sketches.Theta;

/// <summary>
/// Confidence bounds on a distinct count estimated by uniform sampling at rate
/// theta.
/// </summary>
/// <remarks>
/// <para>
/// A Theta sketch retains each distinct value independently with probability
/// theta, so the retained count is binomially distributed and the interesting
/// question — given <c>s</c> retained at rate theta, what range of true counts
/// is plausible — is a binomial confidence interval.
/// </para>
/// <para>
/// Three regimes, because no single approximation covers the range well:
/// with more than 120 samples the Gaussian approximation is good enough; with
/// few samples and a very small theta the Gaussian is used but with a fudged
/// standard-deviation count from <see cref="EquivTables"/>; and in the awkward
/// middle the exact binomial tail is summed directly, which is affordable only
/// because that regime is bounded to small counts.
/// </para>
/// </remarks>
internal static class BinomialBounds
{
    /// <summary>
    /// Tail probability outside <c>n</c> standard deviations of a normal, for
    /// <c>n</c> = 0, 1, 2, 3. Each is <c>0.5 * (1 + erf(-n / sqrt(2)))</c>.
    /// </summary>
    private static readonly double[] DeltaOfNumStdDev =
    [
        0.5000000000000000000,
        0.1586553191586026479,
        0.0227502618904135701,
        0.0013498126861731796,
    ];

    /// <summary>Above this sample count the plain Gaussian approximation is good enough.</summary>
    private const int GaussianSampleThreshold = 120;

    /// <summary>
    /// Returns the lower confidence bound on the true distinct count.
    /// </summary>
    /// <param name="numSamples">Retained entries.</param>
    /// <param name="theta">The sampling rate, in (0, 1].</param>
    /// <param name="numStdDev">1, 2, or 3 — roughly 68%, 95%, or 99.7% confidence.</param>
    /// <param name="noDataSeen">
    /// True for a virgin sketch. It distinguishes "nothing was ever presented",
    /// where both bounds are zero, from "values were presented but none
    /// retained", where an upper bound still exists.
    /// </param>
    public static double LowerBound(int numSamples, double theta, int numStdDev, bool noDataSeen)
    {
        if (noDataSeen)
        {
            return 0.0;
        }

        CheckArgs(numSamples, theta, numStdDev);
        double lb = ApproxBinomialLowerBound(numSamples, theta, numStdDev);
        double estimate = numSamples / theta;

        // Two sanity rails: the bound can never exceed the estimate, and it can
        // never fall below the number of values actually in hand.
        return Math.Min(estimate, Math.Max(numSamples, lb));
    }

    /// <summary>
    /// Returns the upper confidence bound on the true distinct count.
    /// </summary>
    /// <param name="numSamples">Retained entries.</param>
    /// <param name="theta">The sampling rate, in (0, 1].</param>
    /// <param name="numStdDev">1, 2, or 3 — roughly 68%, 95%, or 99.7% confidence.</param>
    /// <param name="noDataSeen">True for a virgin sketch; see <see cref="LowerBound"/>.</param>
    public static double UpperBound(int numSamples, double theta, int numStdDev, bool noDataSeen)
    {
        if (noDataSeen)
        {
            return 0.0;
        }

        CheckArgs(numSamples, theta, numStdDev);
        double ub = ApproxBinomialUpperBound(numSamples, theta, numStdDev);
        double estimate = numSamples / theta;

        return Math.Max(estimate, ub);
    }

    private static void CheckArgs(int numSamples, double theta, int numStdDev)
    {
        if (numStdDev is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numStdDev), numStdDev, "Number of standard deviations must be 1, 2, or 3.");
        }
        if (numSamples < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numSamples), numSamples, "Sample count cannot be negative.");
        }
        if (theta <= 0.0 || theta > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(theta), theta, "Theta must be greater than 0 and at most 1.");
        }
    }

    /// <summary>Gaussian lower bound with a continuity correction.</summary>
    private static double ContinuityCorrectedClassicLowerBound(double numSamples, double theta, double numStdDev)
    {
        double nHat = (numSamples - 0.5) / theta;
        double b = numStdDev * Math.Sqrt((1.0 - theta) / theta);
        double d = 0.5 * b * Math.Sqrt((b * b) + (4.0 * nHat));
        double center = nHat + (0.5 * b * b);
        return center - d;
    }

    /// <summary>Gaussian upper bound with a continuity correction.</summary>
    private static double ContinuityCorrectedClassicUpperBound(double numSamples, double theta, double numStdDev)
    {
        double nHat = (numSamples + 0.5) / theta;
        double b = numStdDev * Math.Sqrt((1.0 - theta) / theta);
        double d = 0.5 * b * Math.Sqrt((b * b) + (4.0 * nHat));
        double center = nHat + (0.5 * b * b);
        return center + d;
    }

    private static double ApproxBinomialLowerBound(int numSamples, double theta, int numStdDev)
    {
        if (theta == 1.0)
        {
            // Nothing was sampled away, so the count is exact.
            return numSamples;
        }
        if (numSamples == 0)
        {
            return 0.0;
        }
        if (numSamples == 1)
        {
            // One sample: solve the geometric tail directly.
            double delta = DeltaOfNumStdDev[numStdDev];
            return Math.Floor(Math.Log(1.0 - delta) / Math.Log(1.0 - theta));
        }
        if (numSamples > GaussianSampleThreshold)
        {
            return ContinuityCorrectedClassicLowerBound(numSamples, theta, numStdDev) - 0.5;
        }

        // 2 <= numSamples <= 120 from here.
        if (theta > 1.0 - 1e-5)
        {
            // Theta is so close to 1 that sampling loss is negligible.
            return numSamples;
        }
        if (theta < numSamples / 360.0)
        {
            // Small theta: Gaussian shape, but with the tabulated effective
            // standard-deviation count in place of the nominal one.
            int index = (3 * numSamples) + (numStdDev - 1);
            return ContinuityCorrectedClassicLowerBound(numSamples, theta, EquivTables.LowerBound(index)) - 0.5;
        }

        // The awkward middle. Sum the exact binomial tail — affordable only
        // because the branches above bound the estimate here to about 360.
        return SpecialNStar(numSamples, theta, DeltaOfNumStdDev[numStdDev]);
    }

    private static double ApproxBinomialUpperBound(int numSamples, double theta, int numStdDev)
    {
        if (theta == 1.0)
        {
            return numSamples;
        }
        if (numSamples == 0)
        {
            // Values may have been presented and all sampled away, so there is
            // still an upper bound even with nothing retained.
            double delta = DeltaOfNumStdDev[numStdDev];
            return Math.Ceiling(Math.Log(delta) / Math.Log(1.0 - theta));
        }
        if (numSamples > GaussianSampleThreshold)
        {
            return ContinuityCorrectedClassicUpperBound(numSamples, theta, numStdDev) + 0.5;
        }

        // 1 <= numSamples <= 120 from here.
        if (theta > 1.0 - 1e-5)
        {
            return numSamples + 1;
        }
        if (theta < numSamples / 360.0)
        {
            int index = (3 * numSamples) + (numStdDev - 1);
            return ContinuityCorrectedClassicUpperBound(numSamples, theta, EquivTables.UpperBound(index)) + 0.5;
        }

        return SpecialNPrimeF(numSamples, theta, DeltaOfNumStdDev[numStdDev]);
    }

    /// <summary>
    /// Walks the posterior over the true count upward from
    /// <paramref name="numSamples"/> until the accumulated probability passes
    /// <paramref name="delta"/>, then backs up one.
    /// </summary>
    /// <remarks>
    /// Deliberately not in log space: the callers restrict this to
    /// <c>numSamples / p &lt; 500</c>, where the terms stay well inside double
    /// range, and the running time is linear in that ratio.
    /// </remarks>
    private static long SpecialNStar(long numSamples, double p, double delta)
    {
        double q = 1.0 - p;
        double curTerm = Math.Pow(p, numSamples);
        double total = curTerm;
        long m = numSamples;

        while (total <= delta)
        {
            curTerm = curTerm * q * m / ((m + 1) - numSamples);
            total += curTerm;
            m += 1;
        }

        // Overshot, so the last value that satisfied the condition is one back.
        return m - 1;
    }

    /// <summary>The upper-tail counterpart of <see cref="SpecialNStar"/>.</summary>
    private static long SpecialNPrimeB(long numSamples, double p, double delta)
    {
        double q = 1.0 - p;
        double oneMinusDelta = 1.0 - delta;
        double curTerm = Math.Pow(p, numSamples);
        double total = curTerm;
        long m = numSamples;

        while (total < oneMinusDelta)
        {
            curTerm = curTerm * q * m / ((m + 1) - numSamples);
            total += curTerm;
            m += 1;
        }

        return m;
    }

    private static long SpecialNPrimeF(long numSamples, double p, double delta) =>
        SpecialNPrimeB(numSamples + 1, p, delta);
}
