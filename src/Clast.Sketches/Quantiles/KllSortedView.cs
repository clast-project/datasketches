// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// The retained items of a quantiles sketch, in sorted order, paired with the
/// cumulative weight each one stands for.
/// </summary>
/// <remarks>
/// <para>
/// This is the form every rank and quantile query actually runs against. The
/// sketch keeps its items grouped by level, each level carrying a different
/// weight; flattening them into one sorted array with running weights turns
/// both queries into a single binary search.
/// </para>
/// <para>
/// The minimum and maximum are forced into the view even when compaction
/// discarded them. Without that, the sketch could report a maximum it had
/// definitely seen while <c>GetQuantile(1.0)</c> returned something smaller.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
/// <typeparam name="TOps">The element operations.</typeparam>
internal sealed class KllSortedView<T, TOps>
    where TOps : struct, IQuantileItemOps<T>
{
    /// <summary>
    /// Natural ranks below this total are snapped to the nearest ten-millionth
    /// before rounding, so that a rank the caller meant to be exact — 0.3 of
    /// 10 items, say — does not fall to the wrong side on a representation
    /// error of one part in 2^52.
    /// </summary>
    private const double TailRoundingFactor = 1e7;

    private readonly T[] _quantiles;
    private readonly long[] _cumWeights;
    private readonly long _totalN;

    public KllSortedView(T[] quantiles, long[] cumWeights, long totalN, T minItem, T maxItem)
    {
        TOps ops = default;
        int lenIn = cumWeights.Length;

        // Splice in the true min and max if compaction lost them.
        bool adjustLow = ops.LessThan(minItem, quantiles[0]);
        bool adjustHigh = ops.LessThan(quantiles[lenIn - 1], maxItem);
        int adjustedLength = lenIn + (adjustLow ? 1 : 0) + (adjustHigh ? 1 : 0);

        if (adjustedLength > lenIn)
        {
            T[] adjQuantiles = new T[adjustedLength];
            long[] adjCumWeights = new long[adjustedLength];
            int offset = adjustLow ? 1 : 0;
            Array.Copy(quantiles, 0, adjQuantiles, offset, lenIn);
            Array.Copy(cumWeights, 0, adjCumWeights, offset, lenIn);

            if (adjustLow)
            {
                // The weights are cumulative, so the next entry needs no fixup.
                adjQuantiles[0] = minItem;
                adjCumWeights[0] = 1;
            }

            if (adjustHigh)
            {
                adjQuantiles[adjustedLength - 1] = maxItem;
                adjCumWeights[adjustedLength - 1] = cumWeights[lenIn - 1];
                adjCumWeights[adjustedLength - 2] = cumWeights[lenIn - 1] - 1;
            }

            _quantiles = adjQuantiles;
            _cumWeights = adjCumWeights;
        }
        else
        {
            _quantiles = quantiles;
            _cumWeights = cumWeights;
        }

        _totalN = totalN;
    }

    public long N => _totalN;

    public int NumRetained => _quantiles.Length;

    public T MinItem => _quantiles[0];

    public T MaxItem => _quantiles[_quantiles.Length - 1];

    /// <summary>The sorted retained items.</summary>
    public T[] Quantiles => (T[])_quantiles.Clone();

    /// <summary>The cumulative weight at each retained item.</summary>
    public long[] CumulativeWeights => (long[])_cumWeights.Clone();

    /// <summary>The item at a given normalized rank.</summary>
    public T GetQuantile(double rank, QuantileSearchCriteria criteria)
    {
        if (rank < 0.0 || rank > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, "A normalized rank must be between 0 and 1.");
        }

        int len = _cumWeights.Length;
        double naturalRank = NaturalRank(rank, _totalN, criteria);
        int index = criteria == QuantileSearchCriteria.Inclusive
            ? InequalitySearch.FindGreaterOrEqual(_cumWeights, 0, len - 1, naturalRank)
            : InequalitySearch.FindGreaterThan(_cumWeights, 0, len - 1, naturalRank);

        // Exclusive search at rank 1.0 finds nothing above it; the answer is the max.
        return index == -1 ? _quantiles[len - 1] : _quantiles[index];
    }

    /// <summary>The normalized rank of a given item.</summary>
    public double GetRank(T quantile, QuantileSearchCriteria criteria)
    {
        int len = _quantiles.Length;
        int index = criteria == QuantileSearchCriteria.Inclusive
            ? InequalitySearch.FindLessOrEqual<T, TOps>(_quantiles, 0, len - 1, quantile)
            : InequalitySearch.FindLessThan<T, TOps>(_quantiles, 0, len - 1, quantile);

        // Nothing at or below the query means a rank of zero.
        return index == -1 ? 0 : (double)_cumWeights[index] / _totalN;
    }

    /// <summary>
    /// The cumulative distribution: the rank at each split point, with a final
    /// bucket of 1.0.
    /// </summary>
    public double[] GetCDF(T[] splitPoints, QuantileSearchCriteria criteria)
    {
        CheckSplitPoints(splitPoints);
        int len = splitPoints.Length + 1;
        double[] buckets = new double[len];
        for (int i = 0; i < len - 1; i++)
        {
            buckets[i] = GetRank(splitPoints[i], criteria);
        }
        buckets[len - 1] = 1.0;
        return buckets;
    }

    /// <summary>
    /// The probability mass in each bucket delimited by the split points — the
    /// differences of <see cref="GetCDF"/>.
    /// </summary>
    public double[] GetPMF(T[] splitPoints, QuantileSearchCriteria criteria)
    {
        double[] buckets = GetCDF(splitPoints, criteria);
        for (int i = buckets.Length - 1; i > 0; i--)
        {
            buckets[i] -= buckets[i - 1];
        }
        return buckets;
    }

    private static void CheckSplitPoints(T[] splitPoints)
    {
        TOps ops = default;
        if (splitPoints is null) { throw new ArgumentNullException(nameof(splitPoints)); }
        for (int i = 0; i < splitPoints.Length - 1; i++)
        {
            if (!ops.LessThan(splitPoints[i], splitPoints[i + 1]))
            {
                throw new ArgumentException(
                    "Split points must be unique and monotonically increasing.", nameof(splitPoints));
            }
        }
    }

    /// <summary>
    /// Converts a normalized rank to the natural rank the weights are indexed
    /// by, rounding toward the side the search criterion needs.
    /// </summary>
    private static double NaturalRank(double normalizedRank, long totalN, QuantileSearchCriteria criteria)
    {
        double naturalRank = normalizedRank * totalN;
        if (totalN <= TailRoundingFactor)
        {
            naturalRank = Math.Round(naturalRank * TailRoundingFactor, MidpointRounding.AwayFromZero)
                / TailRoundingFactor;
        }
        return criteria == QuantileSearchCriteria.Inclusive
            ? (long)Math.Ceiling(naturalRank)
            : (long)Math.Floor(naturalRank);
    }
}
