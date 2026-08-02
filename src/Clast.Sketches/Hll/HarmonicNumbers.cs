// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.hll.HarmonicNumbers from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// Harmonic numbers, used by the linear-counting estimator that covers an HLL
/// sketch's very low range.
/// </summary>
/// <remarks>
/// When most registers are still untouched, the count is better recovered from
/// how many remain empty — the coupon-collector relationship — than from the HLL
/// formula. That inverse is a difference of harmonic numbers.
/// </remarks>
internal static class HarmonicNumbers
{
    private const double EulerMascheroniConstant = 0.577215664901532860606512090082;

    private const int NumExactHarmonicNumbers = 25;

    private static readonly double[] ExactHarmonicNumbers =
    [
        0.0,
        1.0,
        1.5,
        11.0 / 6.0,
        25.0 / 12.0,
        137.0 / 60.0,
        49.0 / 20.0,
        363.0 / 140.0,
        761.0 / 280.0,
        7129.0 / 2520.0,
        7381.0 / 2520.0,
        83711.0 / 27720.0,
        86021.0 / 27720.0,
        1145993.0 / 360360.0,
        1171733.0 / 360360.0,
        1195757.0 / 360360.0,
        2436559.0 / 720720.0,
        42142223.0 / 12252240.0,
        14274301.0 / 4084080.0,
        275295799.0 / 77597520.0,
        55835135.0 / 15519504.0,
        18858053.0 / 5173168.0,
        19093197.0 / 5173168.0,
        444316699.0 / 118982864.0,
        1347822955.0 / 356948592.0,
    ];

    /// <summary>
    /// Estimates how many distinct values set <paramref name="numBitsSet"/> of
    /// <paramref name="bitVectorLength"/> registers.
    /// </summary>
    public static double GetBitMapEstimate(int bitVectorLength, int numBitsSet) =>
        bitVectorLength
            * (HarmonicNumber(bitVectorLength) - HarmonicNumber(bitVectorLength - numBitsSet));

    /// <summary>
    /// The <paramref name="x"/>-th harmonic number: tabulated exactly for small
    /// values, and beyond that an asymptotic expansion whose term count is chosen
    /// to match double precision at the point the table ends.
    /// </summary>
    private static double HarmonicNumber(long x)
    {
        if (x < NumExactHarmonicNumbers)
        {
            return ExactHarmonicNumbers[(int)x];
        }

        double xd = x;
        double invSq = 1.0 / (xd * xd);
        double sum = Math.Log(xd) + EulerMascheroniConstant + (1.0 / (2.0 * xd));

        double pow = invSq;
        sum -= pow * (1.0 / 12.0);
        pow *= invSq;
        sum += pow * (1.0 / 120.0);
        pow *= invSq;
        sum -= pow * (1.0 / 252.0);
        pow *= invSq;
        sum += pow * (1.0 / 240.0);
        return sum;
    }
}
