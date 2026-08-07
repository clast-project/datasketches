// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// A KLL quantiles sketch over <see cref="double"/> values: answers what the
/// median, the 95th percentile, or the rank of any value is, in space that does
/// not grow with the stream.
/// </summary>
/// <remarks>
/// <para>
/// KLL keeps a small sample of the stream where each retained value stands for
/// a known number of values it displaced, so a rank query is a lookup rather
/// than a scan. Error is bounded on the <em>rank</em>, not the value: with the
/// default <c>k</c> of 200, a reported rank is within about 1.33% of the true
/// rank at 99% confidence. There is no bound on how far the returned value is
/// from the true quantile — where the distribution is flat, a small rank error
/// spans a wide range of values.
/// </para>
/// <para>
/// This is the sketch behind Spark's <c>kll_sketch_agg_double</c> and the
/// Druid and Pinot KLL aggregators, and it reads and writes their serialized
/// form.
/// </para>
/// <para>
/// Sketches merge exactly, which is the reason to use one over sorting: a
/// hundred partitions become a hundred cheap merges instead of a global sort.
/// Merging a sketch built with a smaller <c>k</c> degrades accuracy to that
/// smaller <c>k</c>, which <see cref="GetNormalizedRankError"/> reflects.
/// </para>
/// <para>
/// Unlike Theta and HLL, compaction is randomized, so two sketches fed the same
/// values do not generally serialize to identical bytes.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class KllDoublesSketch
{
    private readonly KllSketchCore<double, DoubleOps> _core;

    /// <summary>
    /// Creates an empty sketch.
    /// </summary>
    /// <param name="k">
    /// Controls size and accuracy, between 8 and 65535. Rank error falls
    /// roughly as <c>k^-0.97</c>; the default of 200 gives about 1.33%.
    /// </param>
    public KllDoublesSketch(int k = KllLevels.DefaultK)
        : this(k, KllLevels.DefaultM, null)
    {
    }

    /// <summary>Creates a sketch with an explicit random source, for deterministic tests.</summary>
    internal KllDoublesSketch(int k, int m, Random? random) =>
        _core = new KllSketchCore<double, DoubleOps>(k, m, random);

    private KllDoublesSketch(KllSketchCore<double, DoubleOps> core) => _core = core;

    /// <summary>The configured size and accuracy parameter.</summary>
    public int K => _core.K;

    /// <summary>The number of values presented to the sketch.</summary>
    public long N => _core.N;

    /// <summary>True if the sketch has seen no values.</summary>
    public bool IsEmpty => _core.IsEmpty;

    /// <summary>
    /// True once the sketch has started discarding values. Below this point its
    /// answers are exact.
    /// </summary>
    public bool IsEstimationMode => _core.IsEstimationMode;

    /// <summary>The number of values the sketch is currently retaining.</summary>
    public int NumRetained => _core.NumRetained;

    /// <summary>Bytes <see cref="ToByteArray"/> will produce.</summary>
    public int SerializedSizeBytes => _core.SerializedSizeBytes;

    /// <summary>
    /// The smallest value presented. Always exact — the minimum and maximum are
    /// tracked separately and never discarded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double MinItem => IsEmpty ? throw EmptySketch() : _core.MinItem;

    /// <summary>The largest value presented. Always exact.</summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double MaxItem => IsEmpty ? throw EmptySketch() : _core.MaxItem;

    /// <summary>
    /// Presents a value to the sketch. NaN is ignored, since it has no place in
    /// an ordering.
    /// </summary>
    public void Update(double item)
    {
        if (double.IsNaN(item)) { return; }
        _core.Update(item);
    }

    /// <summary>Merges another sketch into this one.</summary>
    public void Merge(KllDoublesSketch other)
    {
        if (other is null) { throw new ArgumentNullException(nameof(other)); }
        _core.Merge(other._core);
    }

    /// <summary>
    /// The value at a given normalized rank, where 0.0 is the minimum and 1.0
    /// the maximum.
    /// </summary>
    /// <param name="rank">A normalized rank between 0 and 1.</param>
    /// <param name="criteria">Whether the rank includes the value at the boundary.</param>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double GetQuantile(double rank, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetQuantile(rank, criteria);

    /// <summary>The values at several normalized ranks, sharing one sorted view.</summary>
    public double[] GetQuantiles(
        double[] ranks, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive)
    {
        if (ranks is null) { throw new ArgumentNullException(nameof(ranks)); }
        KllSortedView<double, DoubleOps> view = _core.GetSortedView();
        double[] result = new double[ranks.Length];
        for (int i = 0; i < ranks.Length; i++)
        {
            result[i] = view.GetQuantile(ranks[i], criteria);
        }
        return result;
    }

    /// <summary>
    /// The normalized rank of a value: the fraction of the stream at or below
    /// it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double GetRank(double item, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetRank(item, criteria);

    /// <summary>The normalized ranks of several values, sharing one sorted view.</summary>
    public double[] GetRanks(
        double[] items, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive)
    {
        if (items is null) { throw new ArgumentNullException(nameof(items)); }
        KllSortedView<double, DoubleOps> view = _core.GetSortedView();
        double[] result = new double[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            result[i] = view.GetRank(items[i], criteria);
        }
        return result;
    }

    /// <summary>
    /// The cumulative distribution at the given split points: one rank per
    /// split point, plus a trailing 1.0.
    /// </summary>
    /// <param name="splitPoints">Unique values in increasing order.</param>
    /// <param name="criteria">Whether each bucket includes its boundary value.</param>
    public double[] GetCDF(
        double[] splitPoints, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetCDF(splitPoints, criteria);

    /// <summary>
    /// The fraction of the stream falling in each bucket delimited by the split
    /// points — a histogram, normalized.
    /// </summary>
    public double[] GetPMF(
        double[] splitPoints, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetPMF(splitPoints, criteria);

    /// <summary>
    /// The values the sketch retained, in sorted order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pairs index-for-index with <see cref="GetCumulativeWeights"/>: together
    /// they are the sketch's whole answer, and everything else here is derived
    /// from them. Useful for building a histogram in one pass rather than
    /// calling <see cref="GetRank"/> repeatedly.
    /// </para>
    /// <para>
    /// The length can exceed <see cref="NumRetained"/> by up to two. The exact
    /// minimum and maximum are tracked outside the retained set, and are spliced
    /// back in here when compaction discarded them — otherwise the sketch could
    /// report a maximum that <see cref="GetQuantile"/> at rank 1.0 never
    /// returned.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double[] GetRetainedItems() => _core.GetSortedView().Quantiles;

    /// <summary>
    /// The cumulative weight at each retained value: how many stream values are
    /// at or below it. The last entry is always <see cref="N"/>.
    /// </summary>
    /// <remarks>
    /// Same length as <see cref="GetRetainedItems"/>, which may exceed
    /// <see cref="NumRetained"/>; index from the end rather than from
    /// <see cref="NumRetained"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public long[] GetCumulativeWeights() => _core.GetSortedView().CumulativeWeights;

    /// <summary>
    /// This sketch's normalized rank error, accounting for any smaller <c>k</c>
    /// merged into it.
    /// </summary>
    /// <param name="pmf">
    /// True for the double-sided error that applies to <see cref="GetPMF"/> and
    /// <see cref="GetCDF"/>; false for the single-sided error of every other
    /// query.
    /// </param>
    public double GetNormalizedRankError(bool pmf) => KllLevels.NormalizedRankError(_core.MinK, pmf);

    /// <summary>The normalized rank error for a given <c>k</c>.</summary>
    public static double NormalizedRankError(int k, bool pmf) => KllLevels.NormalizedRankError(k, pmf);

    /// <summary>The smallest <c>k</c> that achieves a requested normalized rank error.</summary>
    public static int KFromEpsilon(double epsilon, bool pmf) => KllLevels.KFromEpsilon(epsilon, pmf);

    /// <summary>
    /// Serializes to the compact form, byte-compatible with
    /// <c>datasketches-java</c> and <c>datasketches-cpp</c>.
    /// </summary>
    /// <remarks>
    /// The first rank or quantile query sorts the sketch's newest values as a
    /// side effect, and the serialized form records that they are sorted. An
    /// image taken before any query can therefore differ from one taken after,
    /// in the flags byte and in the order of those values. The two describe the
    /// same distribution and answer every query identically.
    /// </remarks>
    public byte[] ToByteArray() => _core.ToByteArray();

    /// <summary>Reads a serialized KLL doubles sketch.</summary>
    /// <exception cref="ArgumentException">The image is not a well-formed KLL doubles sketch.</exception>
    public static KllDoublesSketch Deserialize(ReadOnlySpan<byte> image) =>
        new(KllSketchCore<double, DoubleOps>.Deserialize(image));

    private static InvalidOperationException EmptySketch() =>
        new("The sketch is empty.");
}
