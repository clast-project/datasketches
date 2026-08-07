// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// Binary searches that return the neighbour on a chosen side of the query
/// rather than requiring an exact hit.
/// </summary>
/// <remarks>
/// <para>
/// A quantile query almost never lands exactly on a retained value, so the
/// interesting question is "which side do we take". These four searches are the
/// four answers, and which one runs is what
/// <see cref="QuantileSearchCriteria"/> selects:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="FindLessThan{T, TOps}"/> — highest index with <c>arr[i] &lt; v</c>.</description></item>
/// <item><description><see cref="FindLessOrEqual{T, TOps}"/> — highest index with <c>arr[i] &lt;= v</c>.</description></item>
/// <item><description><see cref="FindGreaterOrEqual"/> — lowest index with <c>arr[i] &gt;= v</c>.</description></item>
/// <item><description><see cref="FindGreaterThan"/> — lowest index with <c>arr[i] &gt; v</c>.</description></item>
/// </list>
/// <para>
/// Each returns -1 when no index satisfies the relation, which the callers turn
/// into the min or max item. The structure is the reference library's
/// compare / resolve pair: the loop narrows to an adjacent pair, then resolves
/// within it, which keeps the boundary cases in one place instead of spread
/// across the loop conditions.
/// </para>
/// </remarks>
internal static class InequalitySearch
{
    /// <summary>Highest index in <c>[low, high]</c> whose item is strictly less than <paramref name="v"/>.</summary>
    public static int FindLessThan<T, TOps>(T[] arr, int low, int high, T v)
        where TOps : struct, IQuantileItemOps<T>
    {
        TOps ops = default;
        int lo = low;
        int hi = high;
        while (lo <= hi)
        {
            if (hi - lo <= 1)
            {
                // Resolve within the final adjacent pair, preferring the higher index.
                if (ops.LessThan(arr[hi], v)) { return hi; }
                return ops.LessThan(arr[lo], v) ? lo : -1;
            }
            int mid = lo + ((hi - lo) / 2);
            if (!ops.LessThan(arr[mid], v)) { hi = mid; }        // v <= arr[mid]
            else if (ops.LessThan(arr[mid + 1], v)) { lo = mid + 1; }
            else { return mid; }                                  // arr[mid] < v <= arr[mid+1]
        }
        return -1;
    }

    /// <summary>Highest index in <c>[low, high]</c> whose item is less than or equal to <paramref name="v"/>.</summary>
    public static int FindLessOrEqual<T, TOps>(T[] arr, int low, int high, T v)
        where TOps : struct, IQuantileItemOps<T>
    {
        TOps ops = default;
        int lo = low;
        int hi = high;
        while (lo <= hi)
        {
            if (hi - lo <= 1)
            {
                if (!ops.LessThan(v, arr[hi])) { return hi; }
                return !ops.LessThan(v, arr[lo]) ? lo : -1;
            }
            int mid = lo + ((hi - lo) / 2);
            if (ops.LessThan(v, arr[mid])) { hi = mid; }
            else if (!ops.LessThan(v, arr[mid + 1])) { lo = mid + 1; } // arr[mid+1] <= v
            else { return mid; }                                       // arr[mid] <= v < arr[mid+1]
        }
        return -1;
    }

    /// <summary>Lowest index in <c>[low, high]</c> whose weight is greater than or equal to <paramref name="v"/>.</summary>
    /// <remarks>
    /// Searches cumulative weights, which are <c>long</c>, against a natural
    /// rank, which arrives as a <c>double</c>. Ranks stay well inside the range
    /// where a double represents every integer exactly, so the mixed comparison
    /// is safe.
    /// </remarks>
    public static int FindGreaterOrEqual(long[] arr, int low, int high, double v)
    {
        int lo = low;
        int hi = high;
        while (lo <= hi)
        {
            if (hi - lo <= 1)
            {
                if (v <= arr[lo]) { return lo; }
                return v <= arr[hi] ? hi : -1;
            }
            int mid = lo + ((hi - lo) / 2);
            if (v <= arr[mid]) { hi = mid; }
            else if (arr[mid + 1] < v) { lo = mid + 1; }
            else { return mid + 1; }                       // arr[mid] < v <= arr[mid+1]
        }
        return -1;
    }

    /// <summary>Lowest index in <c>[low, high]</c> whose weight is strictly greater than <paramref name="v"/>.</summary>
    public static int FindGreaterThan(long[] arr, int low, int high, double v)
    {
        int lo = low;
        int hi = high;
        while (lo <= hi)
        {
            if (hi - lo <= 1)
            {
                if (v < arr[lo]) { return lo; }
                return v < arr[hi] ? hi : -1;
            }
            int mid = lo + ((hi - lo) / 2);
            if (v < arr[mid]) { hi = mid; }
            else if (arr[mid + 1] <= v) { lo = mid + 1; }
            else { return mid + 1; }                       // arr[mid] <= v < arr[mid+1]
        }
        return -1;
    }
}
