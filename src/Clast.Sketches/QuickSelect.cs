// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// In-place selection of the k-th smallest value, used by the QuickSelect Theta
/// sketch to find the new theta when its hash table overflows.
/// </summary>
/// <remarks>
/// Selection rather than a full sort: the sketch only needs the value at the
/// cut point, so this runs in linear expected time instead of n log n. All
/// methods reorder the array they are given.
/// </remarks>
internal static class QuickSelect
{
    /// <summary>
    /// Returns the <paramref name="pivot"/>-th smallest value (1-based),
    /// counting only the non-zero entries and treating zeros as sorting first.
    /// </summary>
    /// <param name="array">The hash table, reordered in place.</param>
    /// <param name="nonZeros">How many entries of <paramref name="array"/> are non-zero.</param>
    /// <param name="pivot">The 1-based rank to select among the non-zero values.</param>
    /// <returns>The selected value, or 0 if there are fewer than <paramref name="pivot"/> non-zero values.</returns>
    public static long SelectExcludingZeros(long[] array, int nonZeros, int pivot)
    {
        if (pivot > nonZeros)
        {
            return 0L;
        }

        // Zeros are empty slots, and they sort below every real hash, so the
        // rank among non-zeros is offset by however many empty slots there are.
        int zeros = array.Length - nonZeros;
        return Select(array, 0, array.Length - 1, pivot + zeros - 1);
    }

    /// <summary>Returns the value that would sit at index <paramref name="pivot"/> if the range were sorted.</summary>
    private static long Select(long[] array, int lo, int hi, int pivot)
    {
        while (hi > lo)
        {
            int j = Partition(array, lo, hi);
            if (j == pivot)
            {
                return array[pivot];
            }
            if (j > pivot)
            {
                hi = j - 1;
            }
            else
            {
                lo = j + 1;
            }
        }
        return array[pivot];
    }

    /// <summary>
    /// Hoare partition around <c>array[lo]</c>, leaving
    /// <c>array[lo..j-1] &lt;= array[j] &lt;= array[j+1..hi]</c>.
    /// </summary>
    private static int Partition(long[] array, int lo, int hi)
    {
        int i = lo;
        int j = hi + 1;
        long v = array[lo];

        while (true)
        {
            while (array[++i] < v)
            {
                if (i == hi) { break; }
            }
            while (v < array[--j])
            {
                if (j == lo) { break; }
            }
            if (i >= j) { break; }

            (array[i], array[j]) = (array[j], array[i]);
        }

        (array[lo], array[j]) = (array[j], array[lo]);
        return j;
    }
}
