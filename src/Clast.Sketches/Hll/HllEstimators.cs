// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.hll.HllEstimators from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// The estimators that turn HLL register state into a distinct count.
/// </summary>
internal static class HllEstimators
{
    /// <summary>
    /// The composite (non-HIP) estimator: several estimators pasted together,
    /// each covering the range where it behaves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw Flajolet estimator is biased at low cardinalities, so its output
    /// is corrected through a measured table. Below roughly <c>3k</c> the linear
    /// counting estimator is also in play, and rather than switch between them at
    /// a threshold — which introduces bias of its own — the two are averaged and
    /// the <em>average</em> is compared against the crossover point.
    /// </para>
    /// </remarks>
    public static double CompositeEstimate(HllArray array)
    {
        int lgConfigK = array.LgConfigK;
        double rawEst = RawEstimate(lgConfigK, array.KxQ0 + array.KxQ1);

        double[] xArr = CompositeInterpolationXTable.XArrs[lgConfigK - CompositeInterpolationXTable.MinLgK];
        double yStride = CompositeInterpolationXTable.YStrides[lgConfigK - CompositeInterpolationXTable.MinLgK];
        int lastIndex = xArr.Length - 1;

        if (rawEst < xArr[0])
        {
            return 0;
        }

        if (rawEst > xArr[lastIndex])
        {
            // Past the table, extrapolate along the ray through its final point.
            double finalY = yStride * lastIndex;
            return rawEst * (finalY / xArr[lastIndex]);
        }

        double adjEst = CubicInterpolation.UsingXArrAndYStride(xArr, yStride, rawEst);

        // Above 3k the linear estimator can return wild values, so do not even
        // compute it. Empirically safe for 2^4 <= k <= 2^21.
        if (adjEst > (3 << lgConfigK))
        {
            return adjEst;
        }

        double linEst = BitMapEstimate(lgConfigK, array.CurMin, array.NumAtCurMin);
        double avgEst = (adjEst + linEst) / 2.0;

        // Measured crossover between the two estimators' average error.
        double crossOver = lgConfigK switch
        {
            4 => 0.718,
            5 => 0.672,
            _ => 0.64,
        };

        return avgEst > (crossOver * (1 << lgConfigK)) ? adjEst : linEst;
    }

    /// <summary>
    /// Lower confidence bound on an HLL-mode sketch.
    /// </summary>
    /// <remarks>
    /// Floored at the number of non-empty registers: each one was reached by at
    /// least one distinct value, so the true count cannot be below that. The
    /// floor only bites for counts at or below <c>k</c>.
    /// </remarks>
    public static double LowerBound(HllArray array, int numStdDev)
    {
        int configK = 1 << array.LgConfigK;
        double numNonZeros = array.CurMin == 0 ? configK - array.NumAtCurMin : configK;
        double estimate = array.Estimate;
        double relErr = GetRelErr(upperBound: false, array.IsOutOfOrder, array.LgConfigK, numStdDev);
        return Math.Max(estimate / (1.0 + relErr), numNonZeros);
    }

    /// <summary>Upper confidence bound on an HLL-mode sketch.</summary>
    public static double UpperBound(HllArray array, int numStdDev)
    {
        double relErr = GetRelErr(upperBound: true, array.IsOutOfOrder, array.LgConfigK, numStdDev);
        return array.Estimate / (1.0 - relErr);
    }

    /// <summary>
    /// Relative error at a given confidence.
    /// </summary>
    /// <remarks>
    /// Above lgK 12 the asymptotic formula is accurate enough; below it the
    /// bounds are asymmetric and skewed, so measured values are tabulated
    /// instead. The HIP estimator is more accurate than the non-HIP one, hence
    /// the two factors.
    /// </remarks>
    public static double GetRelErr(bool upperBound, bool outOfOrder, int lgConfigK, int numStdDev)
    {
        HllUtil.CheckLgK(lgConfigK);

        if (lgConfigK > 12)
        {
            double rseFactor = outOfOrder ? HllUtil.HllNonHipRseFactor : HllUtil.HllHipRseFactor;
            return numStdDev * rseFactor / Math.Sqrt(1 << lgConfigK);
        }

        return Math.Abs(RelativeErrorTables.GetRelErr(upperBound, outOfOrder, lgConfigK, numStdDev));
    }

    /// <summary>
    /// Linear counting from the number of still-empty registers — the estimator
    /// for the low range, where most registers have not been hit.
    /// </summary>
    private static double BitMapEstimate(int lgConfigK, int curMin, int numAtCurMin)
    {
        int configK = 1 << lgConfigK;
        int numUnhitBuckets = curMin == 0 ? numAtCurMin : 0;

        if (numUnhitBuckets == 0)
        {
            // No empty registers left, so this estimator has nothing to work
            // with; return its saturation value.
            return configK * Math.Log(configK / 0.5);
        }

        return HarmonicNumbers.GetBitMapEstimate(configK, configK - numUnhitBuckets);
    }

    /// <summary>The raw HLL estimator from the 2007 Flajolet paper, figure 3.</summary>
    private static double RawEstimate(int lgConfigK, double kxqSum)
    {
        int configK = 1 << lgConfigK;
        double correctionFactor = lgConfigK switch
        {
            4 => 0.673,
            5 => 0.697,
            6 => 0.709,
            _ => 0.7213 / (1.0 + (1.079 / configK)),
        };
        return correctionFactor * configK * configK / kxqSum;
    }
}
