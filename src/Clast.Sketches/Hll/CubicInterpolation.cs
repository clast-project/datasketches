// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.hll.CubicInterpolation from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// Lagrange cubic interpolation through tabulated correction curves.
/// </summary>
/// <remarks>
/// Both HLL estimators correct a raw value by looking it up in a measured table.
/// Cubic rather than linear interpolation because the curves bend enough that
/// linear error would be visible in the estimate.
/// </remarks>
internal static class CubicInterpolation
{
    /// <summary>Interpolates <paramref name="x"/> through paired x and y tables.</summary>
    public static double UsingXAndYTables(double[] xArr, double[] yArr, double x)
    {
        if (x < xArr[0] || x > xArr[xArr.Length - 1])
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), x, $"Value is outside the table range [{xArr[0]}, {xArr[xArr.Length - 1]}].");
        }

        if (x == xArr[xArr.Length - 1])
        {
            return yArr[yArr.Length - 1];
        }

        int offset = FindStraddle(xArr, x);

        // Interpolation needs four points centred on the straddled interval; at
        // either end of the table it has to lean inward instead.
        int start = offset == 0 ? 0
            : offset == xArr.Length - 2 ? offset - 2
            : offset - 1;

        return Interpolate(
            xArr[start], yArr[start],
            xArr[start + 1], yArr[start + 1],
            xArr[start + 2], yArr[start + 2],
            xArr[start + 3], yArr[start + 3],
            x);
    }

    /// <summary>
    /// Interpolates through an x table whose y values are implicit multiples of
    /// <paramref name="yStride"/>.
    /// </summary>
    public static double UsingXArrAndYStride(double[] xArr, double yStride, double x)
    {
        int lastIndex = xArr.Length - 1;
        if (x == xArr[lastIndex])
        {
            return yStride * lastIndex;
        }

        int offset = FindStraddle(xArr, x);
        int start = offset == 0 ? 0
            : offset == xArr.Length - 2 ? offset - 2
            : offset - 1;

        return Interpolate(
            xArr[start], yStride * start,
            xArr[start + 1], yStride * (start + 1),
            xArr[start + 2], yStride * (start + 2),
            xArr[start + 3], yStride * (start + 3),
            x);
    }

    /// <summary>The cubic through four points, evaluated at <paramref name="x"/>.</summary>
    private static double Interpolate(
        double x0, double y0, double x1, double y1,
        double x2, double y2, double x3, double y3, double x)
    {
        double l0Numer = (x - x1) * (x - x2) * (x - x3);
        double l1Numer = (x - x0) * (x - x2) * (x - x3);
        double l2Numer = (x - x0) * (x - x1) * (x - x3);
        double l3Numer = (x - x0) * (x - x1) * (x - x2);

        double l0Denom = (x0 - x1) * (x0 - x2) * (x0 - x3);
        double l1Denom = (x1 - x0) * (x1 - x2) * (x1 - x3);
        double l2Denom = (x2 - x0) * (x2 - x1) * (x2 - x3);
        double l3Denom = (x3 - x0) * (x3 - x1) * (x3 - x2);

        return (y0 * l0Numer / l0Denom)
             + (y1 * l1Numer / l1Denom)
             + (y2 * l2Numer / l2Denom)
             + (y3 * l3Numer / l3Denom);
    }

    /// <summary>
    /// Binary search for the index whose interval contains <paramref name="x"/>.
    /// </summary>
    /// <remarks>Iterative; the reference recurses, which is equivalent here.</remarks>
    private static int FindStraddle(double[] xArr, double x)
    {
        int left = 0;
        int right = xArr.Length - 1;

        while (left + 1 != right)
        {
            int middle = left + ((right - left) / 2);
            if (xArr[middle] <= x)
            {
                left = middle;
            }
            else
            {
                right = middle;
            }
        }

        return left;
    }
}
