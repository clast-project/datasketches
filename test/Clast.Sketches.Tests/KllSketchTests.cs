using System.Buffers.Binary;
using Clast.Sketches.Quantiles;

namespace Clast.Sketches.Tests;

/// <summary>
/// KLL compaction is randomized, so unlike Theta and HLL a sketch built here
/// cannot be expected to match a reference snapshot byte for byte. What is
/// testable — and what the reference library's own cross-language tests check —
/// is that we read their images correctly, that the invariants hold, and that
/// the answers land inside the sketch's error bound.
/// </summary>
public class KllSketchTests
{
    /// <summary>The counts the TCK snapshots were generated for.</summary>
    public static IEnumerable<object[]> SnapshotCounts()
    {
        foreach (int n in new[] { 0, 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            yield return [n];
        }
    }

    /// <summary>Snapshot counts crossed with the language that produced them.</summary>
    public static IEnumerable<object[]> SnapshotCountsAndLanguages()
    {
        foreach (string language in new[] { "java", "cpp" })
        {
            foreach (object[] row in SnapshotCounts())
            {
                yield return [(int)row[0], language];
            }
        }
    }

    /// <summary>
    /// The snapshots were produced by feeding the integers 1..n, so the true
    /// distribution is uniform on that range and every answer has a known
    /// correct value.
    /// </summary>
    private static KllDoublesSketch BuildDoubles(int n, int k = 200, Random? random = null)
    {
        var sketch = new KllDoublesSketch(k, 8, random);
        for (int i = 1; i <= n; i++) { sketch.Update(i); }
        return sketch;
    }

    private static KllFloatsSketch BuildFloats(int n, int k = 200, Random? random = null)
    {
        var sketch = new KllFloatsSketch(k, 8, random);
        for (int i = 1; i <= n; i++) { sketch.Update(i); }
        return sketch;
    }

    /// <summary>
    /// The total weight, which is the last cumulative weight. Indexed from the
    /// end deliberately: the sorted view splices the exact min and max back in,
    /// so it can be up to two entries longer than <c>NumRetained</c>.
    /// </summary>
    private static long Last(long[] cumulativeWeights) => cumulativeWeights[cumulativeWeights.Length - 1];

    /// <summary>
    /// The tolerance for a rank recovered from a stream of the integers 1..n.
    /// The sketch's own rank error, plus one item's worth: a stream of n
    /// discrete values cannot represent a rank more finely than 1/n, so at
    /// rank 0.0 the answer is the minimum, whose true rank is already 1/n.
    /// </summary>
    private static double RankTolerance(double epsilon, int n) => epsilon + (1.0 / n);

    // ---- Reading the reference images ----

    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void ReadsDoublesSnapshots(int n, string language)
    {
        var sketch = KllDoublesSketch.Deserialize(TckData.Load($"kll_double_n{n}_{language}.sk"));

        Assert.Equal(200, sketch.K);
        Assert.Equal(n, sketch.N);
        Assert.Equal(n == 0, sketch.IsEmpty);
        // 200 items fit exactly; the sketch only starts discarding above that.
        Assert.Equal(n > 200, sketch.IsEstimationMode);

        if (n == 0) { return; }

        Assert.Equal(1.0, sketch.MinItem);
        Assert.Equal(n, sketch.MaxItem);
    }

    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void ReadsFloatsSnapshots(int n, string language)
    {
        var sketch = KllFloatsSketch.Deserialize(TckData.Load($"kll_float_n{n}_{language}.sk"));

        Assert.Equal(200, sketch.K);
        Assert.Equal(n, sketch.N);
        Assert.Equal(n == 0, sketch.IsEmpty);
        Assert.Equal(n > 200, sketch.IsEstimationMode);

        if (n == 0) { return; }

        Assert.Equal(1.0f, sketch.MinItem);
        Assert.Equal(n, sketch.MaxItem);
    }

    /// <summary>
    /// The structural invariant that catches a misread levels array: the
    /// retained items' weights must account for every value in the stream, no
    /// more and no less. A levels array off by one entry fails here even when
    /// n, min and max all look right.
    /// </summary>
    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void SnapshotWeightsSumToN(int n, string language)
    {
        if (n == 0) { return; }

        var sketch = KllDoublesSketch.Deserialize(TckData.Load($"kll_double_n{n}_{language}.sk"));
        double[] items = sketch.GetRetainedItems();
        long[] cumWeights = sketch.GetCumulativeWeights();

        Assert.Equal(items.Length, cumWeights.Length);
        Assert.Equal(n, cumWeights[cumWeights.Length - 1]);

        // Sorted, in range, and monotonically weighted.
        for (int i = 0; i < items.Length; i++)
        {
            Assert.InRange(items[i], sketch.MinItem, sketch.MaxItem);
            if (i > 0)
            {
                Assert.True(items[i - 1] <= items[i], "retained items must be sorted");
                Assert.True(cumWeights[i - 1] < cumWeights[i], "cumulative weights must increase");
            }
        }
    }

    /// <summary>
    /// Re-serializing an image we just read must reproduce it exactly. This is
    /// the strongest byte-level check available for a randomized sketch: it
    /// pins the preamble, the shortened levels array, the min/max placement and
    /// the retained-item ordering all at once, without depending on compaction
    /// making the same coin flips as the reference did.
    /// </summary>
    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void DoublesSnapshotsRoundTripByteForByte(int n, string language)
    {
        byte[] image = TckData.Load($"kll_double_n{n}_{language}.sk");

        byte[] reserialized = KllDoublesSketch.Deserialize(image).ToByteArray();

        Assert.Equal(image, reserialized);
    }

    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void FloatsSnapshotsRoundTripByteForByte(int n, string language)
    {
        byte[] image = TckData.Load($"kll_float_n{n}_{language}.sk");

        byte[] reserialized = KllFloatsSketch.Deserialize(image).ToByteArray();

        Assert.Equal(image, reserialized);
    }

    /// <summary>
    /// Quantiles read out of a reference image must be accurate, which checks
    /// the sorted view and the rank search against data this implementation did
    /// not choose. The stream was 1..n, so the true rank of a value v is v/n and
    /// the error is measured on rank, which is what KLL actually bounds.
    /// </summary>
    [Theory]
    [MemberData(nameof(SnapshotCountsAndLanguages))]
    public void SnapshotQuantilesAreAccurate(int n, string language)
    {
        if (n < 10) { return; }

        var sketch = KllDoublesSketch.Deserialize(TckData.Load($"kll_double_n{n}_{language}.sk"));
        double epsilon = sketch.GetNormalizedRankError(false);

        foreach (double rank in new[] { 0.0, 0.01, 0.25, 0.5, 0.75, 0.95, 0.99, 1.0 })
        {
            double quantile = sketch.GetQuantile(rank);
            double trueRank = quantile / n;
            double tolerance = RankTolerance(epsilon, n);
            Assert.True(
                Math.Abs(trueRank - rank) <= tolerance,
                $"n={n} {language}: rank {rank} returned {quantile}, whose true rank is {trueRank} " +
                $"— off by more than {tolerance}");
        }
    }

    // ---- Behaviour of sketches built here ----

    /// <summary>
    /// Below k items nothing is ever discarded, so the sketch is an exact order
    /// statistic and every answer can be checked against the real one.
    /// </summary>
    [Fact]
    public void IsExactBelowK()
    {
        var sketch = BuildDoubles(200);

        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(200, sketch.NumRetained);
        Assert.Equal(200, sketch.GetCumulativeWeights()[199]);

        // Inclusive: the smallest value whose rank is at least r.
        Assert.Equal(1.0, sketch.GetQuantile(0.0));
        Assert.Equal(100.0, sketch.GetQuantile(0.5));
        Assert.Equal(200.0, sketch.GetQuantile(1.0));

        Assert.Equal(0.5, sketch.GetRank(100.0));
        Assert.Equal(1.0, sketch.GetRank(200.0));
        Assert.Equal(0.005, sketch.GetRank(1.0));
    }

    /// <summary>
    /// The two search criteria differ exactly where a query lands on a retained
    /// value, and this is the case that distinguishes them.
    /// </summary>
    [Fact]
    public void SearchCriteriaDifferOnExactHits()
    {
        var sketch = BuildDoubles(200);

        // Rank 0.5 falls exactly on the 100th value.
        Assert.Equal(100.0, sketch.GetQuantile(0.5, QuantileSearchCriteria.Inclusive));
        Assert.Equal(101.0, sketch.GetQuantile(0.5, QuantileSearchCriteria.Exclusive));

        // The rank of a value counts it (inclusive) or does not (exclusive).
        Assert.Equal(0.5, sketch.GetRank(100.0, QuantileSearchCriteria.Inclusive));
        Assert.Equal(0.495, sketch.GetRank(100.0, QuantileSearchCriteria.Exclusive));
    }

    [Theory]
    [MemberData(nameof(SnapshotCounts))]
    public void EstimatesAreWithinRankError(int n)
    {
        if (n < 10) { return; }

        var sketch = BuildDoubles(n, random: new Random(1));
        double epsilon = sketch.GetNormalizedRankError(false);

        Assert.Equal(n, sketch.N);
        Assert.Equal(1.0, sketch.MinItem);
        Assert.Equal(n, sketch.MaxItem);
        Assert.Equal(n, Last(sketch.GetCumulativeWeights()));

        foreach (double rank in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99 })
        {
            double quantile = sketch.GetQuantile(rank);
            Assert.True(
                Math.Abs((quantile / n) - rank) <= RankTolerance(epsilon, n),
                $"n={n}: rank {rank} returned {quantile}, true rank {quantile / n}, epsilon {epsilon}");
        }
    }

    /// <summary>
    /// Every other test here feeds ascending values, which is the easy case:
    /// level zero arrives already sorted and compaction never has to reorder
    /// anything. A shuffled stream exercises the level-zero sort, the merge of
    /// sorted runs, and the tandem sort in the sorted view — and it is the case
    /// a wrong comparison or an off-by-one in the merge actually shows up in.
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void ShuffledStreamsAreAccurateAtEveryRank(int n)
    {
        int[] values = new int[n];
        for (int i = 0; i < n; i++) { values[i] = i + 1; }
        var shuffle = new Random(20);
        for (int i = n - 1; i > 0; i--)
        {
            int j = shuffle.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        var sketch = new KllDoublesSketch(200, 8, new Random(21));
        foreach (int value in values) { sketch.Update(value); }

        Assert.Equal(n, sketch.N);
        Assert.Equal(1.0, sketch.MinItem);
        Assert.Equal(n, sketch.MaxItem);
        Assert.Equal(n, Last(sketch.GetCumulativeWeights()));

        // Probe the whole rank range rather than a handful of round numbers.
        double epsilon = sketch.GetNormalizedRankError(false);
        double tolerance = RankTolerance(epsilon, n);
        double worst = 0;
        for (int i = 0; i <= 100; i++)
        {
            double rank = i / 100.0;
            double quantile = sketch.GetQuantile(rank);
            worst = Math.Max(worst, Math.Abs((quantile / n) - rank));
        }
        Assert.True(worst <= tolerance, $"n={n}: worst rank error {worst} exceeds {tolerance}");

        // And the inverse direction: the rank of a known value.
        for (int i = 1; i <= 100; i++)
        {
            double value = (double)n * i / 100.0;
            double rank = sketch.GetRank(value);
            Assert.True(Math.Abs(rank - (value / n)) <= tolerance,
                $"n={n}: rank of {value} was {rank}, expected about {value / n}");
        }
    }

    /// <summary>
    /// Space must stay bounded as the stream grows — that is the entire point.
    /// A sketch of a million values retains only a few thousand.
    /// </summary>
    [Fact]
    public void RetainedItemsStayBounded()
    {
        var sketch = BuildDoubles(1_000_000, random: new Random(2));

        Assert.True(sketch.NumRetained < 20 * sketch.K,
            $"retained {sketch.NumRetained} items for k={sketch.K}");
        Assert.True(sketch.SerializedSizeBytes < 50_000,
            $"serialized to {sketch.SerializedSizeBytes} bytes");
    }

    // ---- Merging ----

    /// <summary>
    /// Sketches built independently merge into one that answers for the union of
    /// their streams — the property that makes distributed aggregation possible.
    /// </summary>
    [Fact]
    public void MergePreservesTheCombinedStream()
    {
        var left = new KllDoublesSketch(200, 8, new Random(3));
        var right = new KllDoublesSketch(200, 8, new Random(4));
        for (int i = 1; i <= 100_000; i++)
        {
            if (i % 2 == 0) { left.Update(i); } else { right.Update(i); }
        }

        left.Merge(right);

        Assert.Equal(100_000, left.N);
        Assert.Equal(1.0, left.MinItem);
        Assert.Equal(100_000.0, left.MaxItem);
        Assert.Equal(100_000, Last(left.GetCumulativeWeights()));

        double epsilon = left.GetNormalizedRankError(false);
        foreach (double rank in new[] { 0.1, 0.5, 0.9 })
        {
            double quantile = left.GetQuantile(rank);
            Assert.True(Math.Abs((quantile / 100_000) - rank) <= epsilon,
                $"rank {rank} returned {quantile}");
        }
    }

    [Fact]
    public void MergingManySketchesMatchesOneStream()
    {
        var merged = new KllDoublesSketch(200, 8, new Random(5));
        for (int part = 0; part < 20; part++)
        {
            var partial = new KllDoublesSketch(200, 8, new Random(100 + part));
            for (int i = 1; i <= 5_000; i++) { partial.Update((part * 5_000) + i); }
            merged.Merge(partial);
        }

        Assert.Equal(100_000, merged.N);
        Assert.Equal(1.0, merged.MinItem);
        Assert.Equal(100_000.0, merged.MaxItem);
        Assert.Equal(100_000, Last(merged.GetCumulativeWeights()));

        double epsilon = merged.GetNormalizedRankError(false);
        foreach (double rank in new[] { 0.05, 0.25, 0.5, 0.75, 0.95 })
        {
            double quantile = merged.GetQuantile(rank);
            Assert.True(Math.Abs((quantile / 100_000) - rank) <= epsilon,
                $"rank {rank} returned {quantile}, true rank {quantile / 100_000}, epsilon {epsilon}");
        }
    }

    /// <summary>
    /// Merging a sketch built with a smaller k degrades this sketch's accuracy
    /// to that smaller k, and the reported error must say so.
    /// </summary>
    [Fact]
    public void MergingSmallerKDegradesReportedError()
    {
        var big = BuildDoubles(10_000, k: 400, random: new Random(6));
        var small = BuildDoubles(10_000, k: 100, random: new Random(7));
        double before = big.GetNormalizedRankError(false);

        big.Merge(small);

        Assert.True(big.GetNormalizedRankError(false) > before);
        Assert.Equal(KllDoublesSketch.NormalizedRankError(100, false), big.GetNormalizedRankError(false));
    }

    [Fact]
    public void MergingEmptyChangesNothing()
    {
        var sketch = BuildDoubles(1000, random: new Random(8));
        byte[] before = sketch.ToByteArray();

        sketch.Merge(new KllDoublesSketch());

        Assert.Equal(before, sketch.ToByteArray());
    }

    [Fact]
    public void MergingIntoEmptyAdoptsTheOther()
    {
        var empty = new KllDoublesSketch();
        var source = BuildDoubles(1000, random: new Random(9));

        empty.Merge(source);

        Assert.Equal(1000, empty.N);
        Assert.Equal(1.0, empty.MinItem);
        Assert.Equal(1000.0, empty.MaxItem);
    }

    // ---- Serialization of sketches built here ----

    [Theory]
    [MemberData(nameof(SnapshotCounts))]
    public void SelfBuiltSketchesRoundTrip(int n)
    {
        var sketch = BuildDoubles(n, random: new Random(10));
        byte[] image = sketch.ToByteArray();

        var reread = KllDoublesSketch.Deserialize(image);

        Assert.Equal(image.Length, sketch.SerializedSizeBytes);
        Assert.Equal(sketch.N, reread.N);
        Assert.Equal(sketch.NumRetained, reread.NumRetained);
        Assert.Equal(image, reread.ToByteArray());

        if (n == 0) { return; }

        Assert.Equal(sketch.MinItem, reread.MinItem);
        Assert.Equal(sketch.MaxItem, reread.MaxItem);
        Assert.Equal(sketch.GetRetainedItems(), reread.GetRetainedItems());
        Assert.Equal(sketch.GetCumulativeWeights(), reread.GetCumulativeWeights());
    }

    [Theory]
    [MemberData(nameof(SnapshotCounts))]
    public void FloatsRoundTrip(int n)
    {
        var sketch = BuildFloats(n, random: new Random(11));
        byte[] image = sketch.ToByteArray();

        var reread = KllFloatsSketch.Deserialize(image);

        Assert.Equal(sketch.N, reread.N);
        Assert.Equal(image, reread.ToByteArray());
        if (n > 0)
        {
            Assert.Equal(sketch.MinItem, reread.MinItem);
            Assert.Equal(sketch.MaxItem, reread.MaxItem);
        }
    }

    /// <summary>Floats are half the width, so their images are markedly smaller.</summary>
    [Fact]
    public void FloatsSerializeSmallerThanDoubles()
    {
        var doubles = BuildDoubles(100_000, random: new Random(12));
        var floats = BuildFloats(100_000, random: new Random(12));

        Assert.True(floats.SerializedSizeBytes < doubles.SerializedSizeBytes * 0.6);
    }

    [Fact]
    public void EmptySketchSerializesToEightBytes()
    {
        byte[] image = new KllDoublesSketch().ToByteArray();

        Assert.Equal(8, image.Length);
        Assert.True(KllDoublesSketch.Deserialize(image).IsEmpty);
    }

    [Fact]
    public void SingleItemSketchIsCompact()
    {
        var sketch = new KllDoublesSketch();
        sketch.Update(42.0);

        byte[] image = sketch.ToByteArray();

        Assert.Equal(16, image.Length);   // 8-byte preamble plus the one value
        var reread = KllDoublesSketch.Deserialize(image);
        Assert.Equal(1, reread.N);
        Assert.Equal(42.0, reread.MinItem);
        Assert.Equal(42.0, reread.MaxItem);
        Assert.Equal(42.0, reread.GetQuantile(0.5));
    }

    /// <summary>
    /// The reference library also defines an updatable layout — full levels
    /// array, all items including free space — which its off-heap sketches
    /// write. No TCK snapshot uses it, so this builds one by hand from the
    /// documented layout and checks the reader agrees with the compact image of
    /// the same sketch. The free space is filled with a sentinel that must not
    /// surface in any answer.
    /// </summary>
    [Fact]
    public void ReadsTheUpdatableLayout()
    {
        var source = BuildDoubles(1000, random: new Random(22));
        byte[] compact = source.ToByteArray();

        // Unpack the compact image per the documented layout.
        int k = BinaryPrimitives.ReadUInt16LittleEndian(compact.AsSpan(4));
        int m = compact[6];
        long n = BinaryPrimitives.ReadInt64LittleEndian(compact.AsSpan(8));
        int minK = BinaryPrimitives.ReadUInt16LittleEndian(compact.AsSpan(16));
        int numLevels = compact[18];
        Assert.True(numLevels > 1, "the sketch under test must have more than one level");

        int[] levels = new int[numLevels + 1];
        for (int i = 0; i < numLevels; i++)
        {
            levels[i] = BinaryPrimitives.ReadInt32LittleEndian(compact.AsSpan(20 + (i * 4)));
        }
        // The compact form omits the top boundary; it is the implied capacity.
        int capacity = Clast.Sketches.Quantiles.KllLevels.ComputeTotalItemCapacity(k, m, numLevels);
        levels[numLevels] = capacity;

        int afterLevels = 20 + (numLevels * 4);
        double min = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(compact.AsSpan(afterLevels)));
        double max = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(compact.AsSpan(afterLevels + 8)));
        int retained = capacity - levels[0];
        double[] items = new double[capacity];
        for (int i = 0; i < levels[0]; i++) { items[i] = -999.0; }   // free space sentinel
        for (int i = 0; i < retained; i++)
        {
            items[levels[0] + i] = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(compact.AsSpan(afterLevels + 16 + (i * 8))));
        }

        // Re-lay it out in updatable form: serVer 3, full levels array, all items.
        byte[] updatable = new byte[20 + ((numLevels + 1) * 4) + 16 + (capacity * 8)];
        updatable[0] = 5;                       // preamble ints
        updatable[1] = 3;                       // serialization version: updatable
        updatable[2] = 15;                      // KLL family
        updatable[3] = compact[3];              // flags
        BinaryPrimitives.WriteUInt16LittleEndian(updatable.AsSpan(4), (ushort)k);
        updatable[6] = (byte)m;
        BinaryPrimitives.WriteInt64LittleEndian(updatable.AsSpan(8), n);
        BinaryPrimitives.WriteUInt16LittleEndian(updatable.AsSpan(16), (ushort)minK);
        updatable[18] = (byte)numLevels;
        int offset = 20;
        for (int i = 0; i <= numLevels; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(updatable.AsSpan(offset), levels[i]);
            offset += 4;
        }
        BinaryPrimitives.WriteInt64LittleEndian(updatable.AsSpan(offset), BitConverter.DoubleToInt64Bits(min));
        BinaryPrimitives.WriteInt64LittleEndian(updatable.AsSpan(offset + 8), BitConverter.DoubleToInt64Bits(max));
        offset += 16;
        foreach (double item in items)
        {
            BinaryPrimitives.WriteInt64LittleEndian(updatable.AsSpan(offset), BitConverter.DoubleToInt64Bits(item));
            offset += 8;
        }

        var reread = KllDoublesSketch.Deserialize(updatable);

        Assert.Equal(source.N, reread.N);
        Assert.Equal(source.MinItem, reread.MinItem);
        Assert.Equal(source.MaxItem, reread.MaxItem);
        Assert.Equal(source.NumRetained, reread.NumRetained);
        Assert.Equal(source.GetRetainedItems(), reread.GetRetainedItems());
        Assert.Equal(source.GetCumulativeWeights(), reread.GetCumulativeWeights());
        Assert.DoesNotContain(-999.0, reread.GetRetainedItems());

        // Writing an updatable image back out yields the compact form. Both
        // sides are re-serialized here rather than compared against the image
        // captured above: the queries in between sorted level zero on both
        // sketches, which the level-zero-sorted flag records.
        Assert.Equal(source.ToByteArray(), reread.ToByteArray());
    }

    // ---- Distribution queries ----

    [Fact]
    public void PmfPartitionsTheStream()
    {
        var sketch = BuildDoubles(100_000, random: new Random(13));

        double[] pmf = sketch.GetPMF([25_000.0, 50_000.0, 75_000.0]);

        Assert.Equal(4, pmf.Length);
        Assert.Equal(1.0, pmf.Sum(), 1e-9);
        double epsilon = sketch.GetNormalizedRankError(true);
        foreach (double bucket in pmf)
        {
            Assert.True(Math.Abs(bucket - 0.25) <= 2 * epsilon, $"bucket {bucket} is not near a quarter");
        }
    }

    [Fact]
    public void CdfIsMonotonicAndEndsAtOne()
    {
        var sketch = BuildDoubles(100_000, random: new Random(14));

        double[] cdf = sketch.GetCDF([10_000.0, 50_000.0, 90_000.0]);

        Assert.Equal(4, cdf.Length);
        Assert.Equal(1.0, cdf[3]);
        for (int i = 1; i < cdf.Length; i++)
        {
            Assert.True(cdf[i - 1] <= cdf[i], "the CDF must be non-decreasing");
        }
    }

    [Fact]
    public void SplitPointsMustIncrease()
    {
        var sketch = BuildDoubles(1000, random: new Random(15));

        Assert.Throws<ArgumentException>(() => sketch.GetPMF([5.0, 5.0]));
        Assert.Throws<ArgumentException>(() => sketch.GetCDF([9.0, 2.0]));
    }

    // ---- Edge cases ----

    [Fact]
    public void EmptySketchRejectsQueries()
    {
        var sketch = new KllDoublesSketch();

        Assert.True(sketch.IsEmpty);
        Assert.Equal(0, sketch.N);
        Assert.Throws<InvalidOperationException>(() => sketch.MinItem);
        Assert.Throws<InvalidOperationException>(() => sketch.MaxItem);
        Assert.Throws<InvalidOperationException>(() => sketch.GetQuantile(0.5));
        Assert.Throws<InvalidOperationException>(() => sketch.GetRank(1.0));
    }

    /// <summary>NaN has no place in an ordering, so it is dropped rather than corrupting the sketch.</summary>
    [Fact]
    public void NaNIsIgnored()
    {
        var sketch = new KllDoublesSketch();
        sketch.Update(1.0);
        sketch.Update(double.NaN);
        sketch.Update(3.0);

        Assert.Equal(2, sketch.N);
        Assert.Equal(1.0, sketch.MinItem);
        Assert.Equal(3.0, sketch.MaxItem);
    }

    [Fact]
    public void InfinitiesAreOrdinaryValues()
    {
        var sketch = new KllDoublesSketch();
        sketch.Update(double.NegativeInfinity);
        sketch.Update(0.0);
        sketch.Update(double.PositiveInfinity);

        Assert.Equal(3, sketch.N);
        Assert.Equal(double.NegativeInfinity, sketch.MinItem);
        Assert.Equal(double.PositiveInfinity, sketch.MaxItem);
    }

    [Fact]
    public void RankOutsideTheRangeSaturates()
    {
        var sketch = BuildDoubles(1000, random: new Random(16));

        Assert.Equal(0.0, sketch.GetRank(-1.0));
        Assert.Equal(1.0, sketch.GetRank(10_000.0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void QuantileRankMustBeNormalized(double rank)
    {
        var sketch = BuildDoubles(1000, random: new Random(17));

        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.GetQuantile(rank));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(65_536)]
    public void InvalidKIsRejected(int k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KllDoublesSketch(k));
    }

    [Fact]
    public void SmallKStillWorks()
    {
        var sketch = BuildDoubles(10_000, k: 8, random: new Random(18));

        Assert.Equal(10_000, sketch.N);
        Assert.Equal(1.0, sketch.MinItem);
        Assert.Equal(10_000.0, sketch.MaxItem);
        Assert.Equal(10_000, Last(sketch.GetCumulativeWeights()));
    }

    /// <summary>
    /// A doubles image and a floats image are different formats; reading one as
    /// the other must fail rather than silently return nonsense.
    /// </summary>
    [Fact]
    public void RejectsMalformedImages()
    {
        byte[] valid = BuildDoubles(1000, random: new Random(19)).ToByteArray();

        Assert.Throws<ArgumentException>(() => KllDoublesSketch.Deserialize(new byte[4]));
        Assert.Throws<ArgumentException>(() => KllDoublesSketch.Deserialize(valid.AsSpan(0, 20)));

        byte[] wrongFamily = (byte[])valid.Clone();
        wrongFamily[2] = 7;   // HLL
        Assert.Throws<ArgumentException>(() => KllDoublesSketch.Deserialize(wrongFamily));

        byte[] wrongSerVer = (byte[])valid.Clone();
        wrongSerVer[1] = 9;
        Assert.Throws<ArgumentException>(() => KllDoublesSketch.Deserialize(wrongSerVer));
    }

    // ---- Accuracy parameters ----

    [Fact]
    public void RankErrorMatchesTheReferenceFit()
    {
        // Values from the reference library's fitted curve at the default k.
        Assert.Equal(0.0133, KllDoublesSketch.NormalizedRankError(200, false), 1e-4);
        Assert.Equal(0.0165, KllDoublesSketch.NormalizedRankError(200, true), 1e-4);
    }

    [Fact]
    public void KFromEpsilonRoundTrips()
    {
        foreach (double epsilon in new[] { 0.05, 0.01, 0.005, 0.001 })
        {
            int k = KllDoublesSketch.KFromEpsilon(epsilon, false);
            Assert.True(KllDoublesSketch.NormalizedRankError(k, false) <= epsilon,
                $"k={k} does not achieve epsilon={epsilon}");
        }
    }
}
