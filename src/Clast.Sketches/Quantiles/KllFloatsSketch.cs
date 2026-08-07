// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// A KLL quantiles sketch over <see cref="float"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Identical in behaviour to <see cref="KllDoublesSketch"/>, which documents
/// the algorithm and its error guarantees; only the element type differs. The
/// serialized form is a distinct one — a doubles image and a floats image are
/// not interchangeable, and neither reads the other — so pick the type to match
/// whatever produced the data.
/// </para>
/// <para>
/// Halving the item width halves the retained data, so this is the cheaper
/// choice when the values are already single-precision. It is what Spark's
/// <c>kll_sketch_agg_float</c> produces.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class KllFloatsSketch
{
    private readonly KllSketchCore<float, FloatOps> _core;

    /// <summary>
    /// Creates an empty sketch.
    /// </summary>
    /// <param name="k">
    /// Controls size and accuracy, between 8 and 65535. The default of 200
    /// gives about 1.33% normalized rank error.
    /// </param>
    public KllFloatsSketch(int k = KllLevels.DefaultK)
        : this(k, KllLevels.DefaultM, null)
    {
    }

    /// <summary>Creates a sketch with an explicit random source, for deterministic tests.</summary>
    internal KllFloatsSketch(int k, int m, Random? random) =>
        _core = new KllSketchCore<float, FloatOps>(k, m, random);

    private KllFloatsSketch(KllSketchCore<float, FloatOps> core) => _core = core;

    /// <summary>The configured size and accuracy parameter.</summary>
    public int K => _core.K;

    /// <summary>The number of values presented to the sketch.</summary>
    public long N => _core.N;

    /// <summary>True if the sketch has seen no values.</summary>
    public bool IsEmpty => _core.IsEmpty;

    /// <summary>True once the sketch has started discarding values.</summary>
    public bool IsEstimationMode => _core.IsEstimationMode;

    /// <summary>The number of values the sketch is currently retaining.</summary>
    public int NumRetained => _core.NumRetained;

    /// <summary>Bytes <see cref="ToByteArray"/> will produce.</summary>
    public int SerializedSizeBytes => _core.SerializedSizeBytes;

    /// <summary>The smallest value presented. Always exact.</summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public float MinItem => IsEmpty ? throw EmptySketch() : _core.MinItem;

    /// <summary>The largest value presented. Always exact.</summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public float MaxItem => IsEmpty ? throw EmptySketch() : _core.MaxItem;

    /// <summary>Presents a value to the sketch. NaN is ignored.</summary>
    public void Update(float item)
    {
        if (float.IsNaN(item)) { return; }
        _core.Update(item);
    }

    /// <summary>Merges another sketch into this one.</summary>
    public void Merge(KllFloatsSketch other)
    {
        if (other is null) { throw new ArgumentNullException(nameof(other)); }
        _core.Merge(other._core);
    }

    /// <summary>The value at a given normalized rank, where 0.0 is the minimum and 1.0 the maximum.</summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public float GetQuantile(double rank, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetQuantile(rank, criteria);

    /// <summary>The values at several normalized ranks, sharing one sorted view.</summary>
    public float[] GetQuantiles(
        double[] ranks, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive)
    {
        if (ranks is null) { throw new ArgumentNullException(nameof(ranks)); }
        KllSortedView<float, FloatOps> view = _core.GetSortedView();
        float[] result = new float[ranks.Length];
        for (int i = 0; i < ranks.Length; i++)
        {
            result[i] = view.GetQuantile(ranks[i], criteria);
        }
        return result;
    }

    /// <summary>The normalized rank of a value: the fraction of the stream at or below it.</summary>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public double GetRank(float item, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetRank(item, criteria);

    /// <summary>The normalized ranks of several values, sharing one sorted view.</summary>
    public double[] GetRanks(
        float[] items, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive)
    {
        if (items is null) { throw new ArgumentNullException(nameof(items)); }
        KllSortedView<float, FloatOps> view = _core.GetSortedView();
        double[] result = new double[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            result[i] = view.GetRank(items[i], criteria);
        }
        return result;
    }

    /// <summary>The cumulative distribution at the given split points, plus a trailing 1.0.</summary>
    public double[] GetCDF(
        float[] splitPoints, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetCDF(splitPoints, criteria);

    /// <summary>The fraction of the stream falling in each bucket delimited by the split points.</summary>
    public double[] GetPMF(
        float[] splitPoints, QuantileSearchCriteria criteria = QuantileSearchCriteria.Inclusive) =>
        _core.GetSortedView().GetPMF(splitPoints, criteria);

    /// <summary>
    /// The values the sketch retained, in sorted order. Pairs index-for-index
    /// with <see cref="GetCumulativeWeights"/>.
    /// </summary>
    /// <remarks>
    /// The length can exceed <see cref="NumRetained"/> by up to two, because the
    /// exact minimum and maximum are spliced in when compaction discarded them.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The sketch is empty.</exception>
    public float[] GetRetainedItems() => _core.GetSortedView().Quantiles;

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
    /// As with <see cref="KllDoublesSketch.ToByteArray"/>, an image taken before
    /// any rank or quantile query can differ from one taken after: querying
    /// sorts the sketch's newest values, and the image records that.
    /// </remarks>
    public byte[] ToByteArray() => _core.ToByteArray();

    /// <summary>Reads a serialized KLL floats sketch.</summary>
    /// <exception cref="ArgumentException">The image is not a well-formed KLL floats sketch.</exception>
    public static KllFloatsSketch Deserialize(ReadOnlySpan<byte> image) =>
        new(KllSketchCore<float, FloatOps>.Deserialize(image));

    private static InvalidOperationException EmptySketch() =>
        new("The sketch is empty.");
}
