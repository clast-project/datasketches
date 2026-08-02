using Clast.Sketches.Hll;

namespace Clast.Sketches.Tests;

public class HllSketchTests
{
    private const double Tolerance = 0.03;

    public static IEnumerable<object[]> TypesAndCounts()
    {
        foreach (var type in new[] { TgtHllType.Hll4, TgtHllType.Hll6, TgtHllType.Hll8 })
        {
            foreach (int n in new[] { 0, 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
            {
                yield return [type, n];
            }
        }
    }

    private static string SnapshotName(TgtHllType type, int n) =>
        $"hll{type switch { TgtHllType.Hll4 => 4, TgtHllType.Hll6 => 6, _ => 8 }}_n{n}_java.sk";

    private static HllSketch Build(int n, TgtHllType type, int lgConfigK = 12, long start = 0)
    {
        var sketch = new HllSketch(lgConfigK, type);
        for (long i = 0; i < n; i++)
        {
            sketch.Update(start + i);
        }
        return sketch;
    }

    /// <summary>
    /// The acid test, as for Theta: the TCK snapshots were produced by
    /// datasketches-java from the integers 0..n-1 at the default lgK of 12. If
    /// the hash, the coupon derivation, the mode promotions, the register
    /// packing, the HIP accumulator and the serialization all agree, the bytes
    /// agree — including the doubles, which must match to the last bit.
    /// </summary>
    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void ReproducesJavaSnapshotsByteForByte(TgtHllType type, int n)
    {
        byte[] actual = Build(n, type).ToCompactByteArray();

        Assert.Equal(TckData.Load(SnapshotName(type, n)), actual);
    }

    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void ReadsJavaSnapshots(TgtHllType type, int n)
    {
        var sketch = HllSketch.Deserialize(TckData.Load(SnapshotName(type, n)));

        Assert.Equal(type, sketch.TgtHllType);
        Assert.Equal(12, sketch.LgConfigK);
        Assert.Equal(n == 0, sketch.IsEmpty);
        Assert.Equal(n, sketch.Estimate, Math.Max(n * Tolerance, 1e-9));
    }

    /// <summary>
    /// Round-tripping preserves the sketch exactly — the estimate is bit-identical,
    /// not merely close.
    /// </summary>
    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void SnapshotsRoundTripSemantically(TgtHllType type, int n)
    {
        byte[] image = TckData.Load(SnapshotName(type, n));
        var reread = HllSketch.Deserialize(image);

        Assert.Equal(type, reread.TgtHllType);
        Assert.Equal(HllSketch.Deserialize(image).Estimate, reread.Estimate);
        Assert.Equal(reread.Estimate, HllSketch.Deserialize(reread.ToCompactByteArray()).Estimate);
    }

    /// <summary>
    /// LIST and HLL mode images round-trip byte-for-byte. SET mode deliberately
    /// does not, and is covered separately.
    /// </summary>
    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void ListAndHllSnapshotsRoundTripByteForByte(TgtHllType type, int n)
    {
        if (n is 10 or 100)
        {
            return; // SET mode; see SetModeRoundTripPreservesTheCouponSetButNotItsOrder.
        }

        byte[] image = TckData.Load(SnapshotName(type, n));

        Assert.Equal(image, HllSketch.Deserialize(image).ToCompactByteArray());
    }

    /// <summary>
    /// A compact SET-mode image stores only the occupied hash-table entries, so
    /// rebuilding replays them in table order rather than the order they were
    /// originally inserted. Open addressing settles collisions by insertion
    /// order, so the rebuilt table lays the same coupons out differently. The
    /// sketch is unchanged — same coupons, bit-identical estimate — but the bytes
    /// are not stable, and the reference implementation behaves the same way.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void SetModeRoundTripPreservesTheCouponSetButNotItsOrder(int n)
    {
        byte[] original = TckData.Load(SnapshotName(TgtHllType.Hll8, n));
        byte[] rebuilt = HllSketch.Deserialize(original).ToCompactByteArray();

        Assert.Equal(original.Length, rebuilt.Length);
        Assert.NotEqual(original, rebuilt);

        Assert.Equal(SortedCoupons(original), SortedCoupons(rebuilt));
        Assert.Equal(
            HllSketch.Deserialize(original).Estimate,
            HllSketch.Deserialize(rebuilt).Estimate);
    }

    /// <summary>Reads the coupon ints out of a compact SET-mode image, in sorted order.</summary>
    private static int[] SortedCoupons(byte[] image)
    {
        int count = BitConverter.ToInt32(image, 8);
        var coupons = new int[count];
        for (int i = 0; i < count; i++)
        {
            coupons[i] = BitConverter.ToInt32(image, 12 + (i * 4));
        }
        Array.Sort(coupons);
        return coupons;
    }

    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void UpdatableFormRoundTrips(TgtHllType type, int n)
    {
        var sketch = Build(n, type);

        byte[] updatable = sketch.ToUpdatableByteArray();
        var reread = HllSketch.Deserialize(updatable);

        Assert.Equal(sketch.Estimate, reread.Estimate);
        Assert.Equal(updatable.Length, sketch.UpdatableSerializationBytes);
        // Both forms describe the same sketch, so they compact identically.
        Assert.Equal(sketch.ToCompactByteArray(), reread.ToCompactByteArray());
    }

    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void DeserializedSketchKeepsAccepting(TgtHllType type, int n)
    {
        var reread = HllSketch.Deserialize(Build(n, type).ToUpdatableByteArray());
        for (long i = n; i < n + 1000; i++)
        {
            reread.Update(i);
        }

        Assert.Equal(n + 1000, reread.Estimate, (n + 1000) * Tolerance);
    }

    [Fact]
    public void AllThreeTypesAgreeOnTheEstimate()
    {
        // The three are isomorphic: same lgK and same input must give the same
        // estimate, differing only in stored size.
        var hll4 = Build(1_000_000, TgtHllType.Hll4);
        var hll6 = Build(1_000_000, TgtHllType.Hll6);
        var hll8 = Build(1_000_000, TgtHllType.Hll8);

        Assert.Equal(hll8.Estimate, hll4.Estimate);
        Assert.Equal(hll8.Estimate, hll6.Estimate);

        Assert.True(hll4.CompactSerializationBytes < hll6.CompactSerializationBytes);
        Assert.True(hll6.CompactSerializationBytes < hll8.CompactSerializationBytes);
    }

    [Fact]
    public void EmptySketchIsEightBytes()
    {
        var sketch = new HllSketch();

        Assert.True(sketch.IsEmpty);
        Assert.Equal(0.0, sketch.Estimate);
        Assert.Equal(8, sketch.CompactSerializationBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void SmallCountsAreEssentiallyExact(int n)
    {
        // Coupon mode stores every distinct value, so the only error is the
        // vanishing chance that two values share a coupon. The estimator still
        // interpolates rather than counting, so this is not exact to the bit —
        // but it is exact to several decimal places, and never below n.
        var sketch = Build(n, TgtHllType.Hll8);

        Assert.True(sketch.Estimate >= n);
        Assert.Equal(n, sketch.Estimate, n * 1e-6);
    }

    [Fact]
    public void SketchGrowsThroughItsModes()
    {
        // Size should step up as the representation changes, never shrink.
        var sketch = new HllSketch(12, TgtHllType.Hll8);
        int previous = sketch.CompactSerializationBytes;
        var sizes = new SortedSet<int>();

        for (long i = 0; i < 20_000; i++)
        {
            sketch.Update(i);
            int size = sketch.CompactSerializationBytes;
            Assert.True(size >= previous, $"Serialized size shrank at {i} updates.");
            sizes.Add(size);
            previous = size;
        }

        // A handful of bytes at the start, the full register array at the end.
        Assert.True(sizes.Min < 64);
        Assert.Equal(HllPreambleBytes + 4096, sizes.Max);
    }

    private const int HllPreambleBytes = 40;

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void DuplicatesDoNotInflateTheEstimate(int n)
    {
        var sketch = new HllSketch();
        for (int pass = 0; pass < 3; pass++)
        {
            for (long i = 0; i < n; i++)
            {
                sketch.Update(i);
            }
        }

        Assert.Equal(n, sketch.Estimate, n * Tolerance);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(21)]
    public void EstimatesAreAccurateAcrossLgK(int lgConfigK)
    {
        const int n = 100_000;
        var sketch = Build(n, TgtHllType.Hll4, lgConfigK);

        // Error scales as 1/sqrt(k), so a tiny sketch is legitimately coarse.
        double allowed = 4.0 / Math.Sqrt(1 << lgConfigK);
        Assert.Equal(n, sketch.Estimate, n * allowed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(22)]
    public void RejectsInvalidLgK(int lgConfigK)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HllSketch(lgConfigK));
    }

    [Fact]
    public void RejectsUnknownTargetType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HllSketch(12, (TgtHllType)99));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IgnoresNullAndEmptyStrings(string? value)
    {
        var sketch = new HllSketch();
        sketch.Update(value);

        Assert.True(sketch.IsEmpty);
    }

    [Fact]
    public void IgnoresEmptySpans()
    {
        var sketch = new HllSketch();
        sketch.Update(ReadOnlySpan<byte>.Empty);
        sketch.Update(ReadOnlySpan<long>.Empty);

        Assert.True(sketch.IsEmpty);
    }

    [Fact]
    public void StringsHashAsTheirUtf8Bytes()
    {
        var fromString = new HllSketch();
        fromString.Update("café");

        var fromBytes = new HllSketch();
        fromBytes.Update(System.Text.Encoding.UTF8.GetBytes("café"));

        Assert.Equal(fromString.ToCompactByteArray(), fromBytes.ToCompactByteArray());
    }

    [Fact]
    public void NegativeZeroAndNaNsAreCanonicalized()
    {
        var sketch = new HllSketch();
        sketch.Update(0.0);
        sketch.Update(-0.0);
        sketch.Update(double.NaN);
        sketch.Update(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001)));

        Assert.Equal(2.0, sketch.Estimate, 1e-5);
    }

    [Theory]
    [MemberData(nameof(TypesAndCounts))]
    public void CopyIsIndependent(TgtHllType type, int n)
    {
        var sketch = Build(n, type);
        var copy = sketch.Copy();

        for (long i = 1_000_000; i < 1_010_000; i++)
        {
            copy.Update(i);
        }

        Assert.Equal(TckData.Load(SnapshotName(type, n)), sketch.ToCompactByteArray());
        Assert.True(copy.Estimate > sketch.Estimate);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void BoundsContainTheTrueCount(int n)
    {
        var sketch = Build(n, TgtHllType.Hll4);

        Assert.InRange(n, sketch.GetLowerBound(3), sketch.GetUpperBound(3));
        Assert.True(sketch.GetLowerBound(1) >= sketch.GetLowerBound(2));
        Assert.True(sketch.GetUpperBound(1) <= sketch.GetUpperBound(2));
    }

    [Fact]
    public void EmptySketchHasZeroBounds()
    {
        var sketch = new HllSketch();

        Assert.Equal(0.0, sketch.GetLowerBound());
        Assert.Equal(0.0, sketch.GetUpperBound());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsInvalidConfidenceLevel(int numStdDev)
    {
        var sketch = Build(100_000, TgtHllType.Hll8);

        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.GetLowerBound(numStdDev));
        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.GetUpperBound(numStdDev));
    }

    [Fact]
    public void RejectsCorruptImages()
    {
        byte[] image = TckData.Load("hll8_n100000_java.sk");

        Assert.Throws<InvalidDataException>(() => HllSketch.Deserialize(image.AsSpan(0, 4)));

        byte[] badFamily = (byte[])image.Clone();
        badFamily[2] = 3;
        Assert.Throws<InvalidDataException>(() => HllSketch.Deserialize(badFamily));

        byte[] badSerVer = (byte[])image.Clone();
        badSerVer[1] = 9;
        Assert.Throws<InvalidDataException>(() => HllSketch.Deserialize(badSerVer));

        Assert.Throws<InvalidDataException>(
            () => HllSketch.Deserialize(image.AsSpan(0, image.Length - 8)));
    }

    /// <summary>
    /// HLL_4 stores register values relative to a running minimum, and the few
    /// that outrun four bits go to a side table. Reaching that path needs a large
    /// enough count that the minimum has risen well above zero.
    /// </summary>
    [Fact]
    public void Hll4HandlesAuxiliaryExceptions()
    {
        var sketch = Build(10_000_000, TgtHllType.Hll4, lgConfigK: 11);

        Assert.Equal(10_000_000, sketch.Estimate, 10_000_000 * 0.10);

        byte[] image = sketch.ToCompactByteArray();
        var reread = HllSketch.Deserialize(image);

        // The exceptions must survive the round trip, both forms of it.
        Assert.Equal(sketch.Estimate, reread.Estimate);
        Assert.Equal(image, reread.ToCompactByteArray());
        Assert.Equal(
            image,
            HllSketch.Deserialize(sketch.ToUpdatableByteArray()).ToCompactByteArray());
        // An auxiliary table makes the image larger than the bare register array.
        Assert.True(image.Length > HllPreambleBytes + (1 << 10));
    }
}
