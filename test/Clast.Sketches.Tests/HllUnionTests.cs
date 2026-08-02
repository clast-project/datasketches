using Clast.Sketches.Hll;

namespace Clast.Sketches.Tests;

public class HllUnionTests
{
    private const double Tolerance = 0.03;

    private static HllSketch Build(long start, int count, TgtHllType type = TgtHllType.Hll4, int lgConfigK = 12)
    {
        var sketch = new HllSketch(lgConfigK, type);
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch;
    }

    [Fact]
    public void UnionOfNothingIsEmpty()
    {
        var union = new HllUnion();

        Assert.True(union.IsEmpty);
        Assert.Equal(0.0, union.Estimate);
        Assert.True(union.GetResult().IsEmpty);
    }

    [Fact]
    public void UnionWithEmptyIsIdentity()
    {
        var union = new HllUnion();
        union.Update(Build(0, 100_000));
        double before = union.Estimate;

        union.Update(new HllSketch());
        union.Update((HllSketch?)null);
        union.Update((HllUnion?)null);

        Assert.Equal(before, union.Estimate);
    }

    [Theory]
    [InlineData(TgtHllType.Hll4)]
    [InlineData(TgtHllType.Hll6)]
    [InlineData(TgtHllType.Hll8)]
    public void UnionOfDisjointSketchesAddsUp(TgtHllType type)
    {
        var union = new HllUnion();
        union.Update(Build(0, 300_000, type));
        union.Update(Build(300_000, 300_000, type));
        union.Update(Build(600_000, 300_000, type));

        Assert.Equal(900_000, union.Estimate, 900_000 * Tolerance);
    }

    [Fact]
    public void UnionOfIdenticalSketchesDoesNotDoubleCount()
    {
        var sketch = Build(0, 500_000);
        var union = new HllUnion();
        union.Update(sketch);
        union.Update(sketch);
        union.Update(sketch);

        Assert.Equal(500_000, union.Estimate, 500_000 * Tolerance);
    }

    [Fact]
    public void UnionOfOverlappingSketchesCountsTheOverlapOnce()
    {
        var union = new HllUnion();
        union.Update(Build(0, 600_000));
        union.Update(Build(400_000, 600_000));

        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * Tolerance);
    }

    /// <summary>
    /// Registers hold a maximum, so merging is idempotent and associative: the
    /// merged result should match a sketch built over all the data directly, not
    /// merely be close to it.
    /// </summary>
    [Fact]
    public void MergingMatchesASingleSketchOverTheSameData()
    {
        var union = new HllUnion();
        for (int part = 0; part < 10; part++)
        {
            union.Update(Build(part * 100_000L, 100_000, TgtHllType.Hll8));
        }

        var merged = union.GetResult(TgtHllType.Hll8);
        var single = Build(0, 1_000_000, TgtHllType.Hll8);

        // Same registers, so the composite estimator gives exactly the same
        // number. Only HIP differs, and a merged sketch cannot use it.
        Assert.Equal(single.CompositeEstimate, merged.CompositeEstimate);
    }

    /// <summary>
    /// Register-wise maximum is commutative and associative, so merge order
    /// cannot change the registers or the estimate.
    /// </summary>
    /// <remarks>
    /// The serialized images are <em>not</em> byte-identical, and should not be:
    /// the HIP accumulator is carried over from whichever input the union
    /// adopted, which depends on order. It is dead state for a merged sketch —
    /// the out-of-order flag forces the composite estimator — so only the
    /// registers and the estimate are the real invariant.
    /// </remarks>
    [Fact]
    public void MergeOrderDoesNotMatter()
    {
        var parts = new[]
        {
            Build(0, 200_000),
            Build(200_000, 50_000),
            Build(250_000, 400_000),
            Build(650_000, 1000),
        };

        var forward = new HllUnion();
        foreach (var part in parts) { forward.Update(part); }

        var backward = new HllUnion();
        for (int i = parts.Length - 1; i >= 0; i--) { backward.Update(parts[i]); }

        byte[] forwardImage = forward.GetResult(TgtHllType.Hll8).ToCompactByteArray();
        byte[] backwardImage = backward.GetResult(TgtHllType.Hll8).ToCompactByteArray();

        const int registersStart = 40;
        Assert.Equal(
            forwardImage.AsSpan(registersStart).ToArray(),
            backwardImage.AsSpan(registersStart).ToArray());
        Assert.Equal(forward.Estimate, backward.Estimate);

        // Everything outside the HIP accumulator at bytes 8..15 agrees too.
        Assert.Equal(
            forwardImage.AsSpan(0, 8).ToArray(),
            backwardImage.AsSpan(0, 8).ToArray());
        Assert.Equal(
            forwardImage.AsSpan(16, registersStart - 16).ToArray(),
            backwardImage.AsSpan(16, registersStart - 16).ToArray());
    }

    [Fact]
    public void MergedResultFallsBackToTheCompositeEstimator()
    {
        // HIP needs every update observed in order, which a merge has not, so the
        // result must mark itself out-of-order and use the composite estimator.
        var union = new HllUnion();
        union.Update(Build(0, 500_000, TgtHllType.Hll8));
        union.Update(Build(500_000, 500_000, TgtHllType.Hll8));

        var result = union.GetResult(TgtHllType.Hll8);

        Assert.Equal(result.CompositeEstimate, result.Estimate);
        Assert.Equal(1_000_000, result.Estimate, 1_000_000 * Tolerance);
    }

    [Theory]
    [InlineData(TgtHllType.Hll4)]
    [InlineData(TgtHllType.Hll6)]
    [InlineData(TgtHllType.Hll8)]
    public void ResultCanBeReturnedAsAnyType(TgtHllType type)
    {
        var union = new HllUnion();
        union.Update(Build(0, 500_000));
        union.Update(Build(500_000, 500_000));

        var result = union.GetResult(type);

        Assert.Equal(type, result.TgtHllType);
        Assert.Equal(1_000_000, result.Estimate, 1_000_000 * Tolerance);
        // The widths are isomorphic, so converting must not change the estimate.
        Assert.Equal(union.GetResult(TgtHllType.Hll8).Estimate, result.Estimate);
    }

    [Fact]
    public void MixedInputTypesMergeCorrectly()
    {
        var union = new HllUnion();
        union.Update(Build(0, 300_000, TgtHllType.Hll4));
        union.Update(Build(300_000, 300_000, TgtHllType.Hll6));
        union.Update(Build(600_000, 300_000, TgtHllType.Hll8));

        Assert.Equal(900_000, union.Estimate, 900_000 * Tolerance);
    }

    /// <summary>
    /// A coarser input caps the result: accuracy the input never had cannot be
    /// recovered, so the union folds down to match it.
    /// </summary>
    [Fact]
    public void CoarserInputPullsTheResultDown()
    {
        var union = new HllUnion(lgMaxK: 14);
        union.Update(Build(0, 500_000, TgtHllType.Hll8, lgConfigK: 14));
        Assert.Equal(14, union.LgConfigK);

        union.Update(Build(500_000, 500_000, TgtHllType.Hll8, lgConfigK: 10));

        Assert.Equal(10, union.LgConfigK);
        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * 0.10);
    }

    /// <summary>An input finer than the union's maximum is folded down to it.</summary>
    [Fact]
    public void FinerInputIsFoldedToTheConfiguredMaximum()
    {
        var union = new HllUnion(lgMaxK: 10);
        union.Update(Build(0, 500_000, TgtHllType.Hll8, lgConfigK: 16));

        Assert.Equal(10, union.LgConfigK);
        Assert.Equal(500_000, union.Estimate, 500_000 * 0.15);
    }

    [Fact]
    public void SmallSketchesMergeThroughTheCouponModes()
    {
        // Exercises the list and set paths, and the reverse merge where a coupon
        // gadget meets an HLL-mode source.
        var union = new HllUnion();
        union.Update(Build(0, 3));
        union.Update(Build(3, 5));
        union.Update(Build(8, 50));
        union.Update(Build(58, 100_000));
        union.Update(Build(100_058, 4));

        Assert.Equal(100_062, union.Estimate, 100_062 * Tolerance);
    }

    [Fact]
    public void UnionOfSmallSketchesIsExact()
    {
        // Entirely within coupon mode, where the sketch stores every value.
        var union = new HllUnion();
        union.Update(Build(0, 3));
        union.Update(Build(3, 3));

        Assert.Equal(6, union.Estimate, 6 * 1e-6);
    }

    [Fact]
    public void UnionsCanBeMergedIntoEachOther()
    {
        var left = new HllUnion();
        left.Update(Build(0, 400_000));

        var right = new HllUnion();
        right.Update(Build(400_000, 400_000));

        left.Update(right);

        Assert.Equal(800_000, left.Estimate, 800_000 * Tolerance);
    }

    [Fact]
    public void AcceptsSerializedSketches()
    {
        var union = new HllUnion();
        union.Update(Build(0, 500_000).ToCompactByteArray());
        union.Update(Build(500_000, 500_000).ToUpdatableByteArray());

        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void MergesTckSnapshots()
    {
        // Real reference bytes, nested so the union is the largest of them.
        var union = new HllUnion();
        foreach (int n in new[] { 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            union.Update(TckData.Load($"hll4_n{n}_java.sk"));
        }

        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void MergingAcrossTypesFromSnapshotsAgrees()
    {
        // The same data serialized three ways must union to the same count.
        var union = new HllUnion();
        union.Update(TckData.Load("hll4_n1000000_java.sk"));
        union.Update(TckData.Load("hll6_n1000000_java.sk"));
        union.Update(TckData.Load("hll8_n1000000_java.sk"));

        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void GetResultIsRepeatableAndNonDestructive()
    {
        var union = new HllUnion();
        union.Update(Build(0, 500_000));

        byte[] first = union.GetResult().ToCompactByteArray();
        Assert.Equal(first, union.GetResult().ToCompactByteArray());

        union.Update(Build(500_000, 500_000));
        Assert.Equal(1_000_000, union.Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void ResultRoundTripsThroughSerialization()
    {
        var union = new HllUnion();
        union.Update(Build(0, 500_000));
        union.Update(Build(500_000, 500_000));

        foreach (var type in new[] { TgtHllType.Hll4, TgtHllType.Hll6, TgtHllType.Hll8 })
        {
            byte[] image = union.GetResult(type).ToCompactByteArray();
            var reread = HllSketch.Deserialize(image);

            Assert.Equal(union.GetResult(type).Estimate, reread.Estimate);
            Assert.Equal(image, reread.ToCompactByteArray());
        }
    }

    [Fact]
    public void ResetReturnsUnionToEmpty()
    {
        var union = new HllUnion();
        union.Update(Build(0, 500_000));
        union.Reset();

        Assert.True(union.IsEmpty);
        Assert.Equal(0.0, union.Estimate);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void BoundsContainTheTrueCount(int n)
    {
        var union = new HllUnion();
        union.Update(Build(0, n / 2));
        union.Update(Build(n / 2, n - (n / 2)));

        Assert.InRange(n, union.GetLowerBound(3), union.GetUpperBound(3));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(22)]
    public void RejectsInvalidLgMaxK(int lgMaxK)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HllUnion(lgMaxK));
    }
}
