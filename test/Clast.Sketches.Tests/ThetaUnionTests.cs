using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

public class ThetaUnionTests
{
    private const double Tolerance = 0.03;

    private static CompactThetaSketch SketchOver(long start, long count, int nominalEntries = 4096)
    {
        var sketch = UpdateThetaSketch.Builder().SetNominalEntries(nominalEntries).Build();
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch.Compact();
    }

    [Fact]
    public void UnionOfNothingIsEmpty()
    {
        var result = new ThetaUnion().GetResult();

        Assert.True(result.IsEmpty);
        Assert.Equal(0.0, result.Estimate);
        Assert.Equal(TckData.Load("theta_n0_java.sk"), result.ToByteArray());
    }

    [Fact]
    public void UnionWithEmptyIsIdentity()
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 1000));
        union.Union(SketchOver(0, 0));
        union.Union(null);

        Assert.Equal(1000, union.GetResult().Estimate, 1000 * Tolerance);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void UnionOfOneExactSketchReproducesItExactly(int n)
    {
        var sketch = SketchOver(0, n);
        var union = new ThetaUnion();
        union.Union(sketch);

        // Below k nothing is sampled away, so the union is lossless.
        Assert.Equal(sketch.ToByteArray(), union.GetResult().ToByteArray());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void UnionOfDisjointSketchesAddsUp(int each)
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, each));
        union.Union(SketchOver(each, each));
        union.Union(SketchOver(2L * each, each));

        Assert.Equal(3.0 * each, union.GetResult().Estimate, 3.0 * each * Tolerance);
    }

    [Fact]
    public void UnionOfIdenticalSketchesDoesNotDoubleCount()
    {
        var sketch = SketchOver(0, 100_000);
        var union = new ThetaUnion();
        union.Union(sketch);
        union.Union(sketch);
        union.Union(sketch);

        Assert.Equal(100_000, union.GetResult().Estimate, 100_000 * Tolerance);
    }

    [Fact]
    public void UnionOfOverlappingSketchesCountsTheOverlapOnce()
    {
        // 0..149,999 and 100,000..249,999 overlap by half of each.
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 150_000));
        union.Union(SketchOver(100_000, 150_000));

        Assert.Equal(250_000, union.GetResult().Estimate, 250_000 * Tolerance);
    }

    /// <summary>
    /// The TCK has no union snapshots, but nested exact ones give a byte-level
    /// check anyway: snapshot n covers 0..n-1, so merging a smaller into a larger
    /// must reproduce the larger exactly. Both are below k, so nothing is
    /// sampled away and there is no slack for an off-by-one to hide in.
    /// </summary>
    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 100)]
    [InlineData(100, 1000)]
    [InlineData(1, 1000)]
    public void MergingNestedExactSnapshotsReproducesTheLargerExactly(int small, int large)
    {
        byte[] expected = TckData.Load($"theta_n{large}_java.sk");

        var union = new ThetaUnion();
        union.UnionCompactImage(TckData.Load($"theta_n{small}_java.sk"));
        union.UnionCompactImage(expected);

        Assert.Equal(expected, union.GetResult().ToByteArray());
    }

    [Fact]
    public void UnionOfNestedTckSnapshotsEstimatesTheLargest()
    {
        // Each snapshot covers 0..n-1, so they are nested and the union is the
        // largest of them. Real reference bytes on the way in.
        var union = new ThetaUnion();
        foreach (int n in new[] { 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            union.UnionCompactImage(TckData.Load($"theta_n{n}_java.sk"));
        }

        Assert.Equal(1_000_000, union.GetResult().Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void MergingReferenceSketchesMatchesASingleSketchOverTheSameData()
    {
        // The point of the whole family: sketches built separately merge to
        // something as good as one sketch built over everything.
        var union = new ThetaUnion();
        for (int part = 0; part < 10; part++)
        {
            union.Union(SketchOver(part * 100_000L, 100_000));
        }

        var merged = union.GetResult();
        var single = SketchOver(0, 1_000_000);

        Assert.Equal(1_000_000, merged.Estimate, 1_000_000 * Tolerance);
        Assert.Equal(single.Estimate, merged.Estimate, 1_000_000 * Tolerance);
    }

    [Fact]
    public void OrderOfMergingDoesNotChangeTheEstimate()
    {
        var parts = new[]
        {
            SketchOver(0, 200_000),
            SketchOver(200_000, 50_000),
            SketchOver(250_000, 400_000),
            SketchOver(650_000, 1000),
        };

        var forward = new ThetaUnion();
        foreach (var part in parts)
        {
            forward.Union(part);
        }

        var backward = new ThetaUnion();
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            backward.Union(parts[i]);
        }

        Assert.Equal(forward.GetResult().Estimate, backward.GetResult().Estimate);
    }

    [Fact]
    public void ResultIsTrimmedToNominalEntries()
    {
        var union = new ThetaUnion(nominalEntries: 1024);
        union.Union(SketchOver(0, 500_000));
        union.Union(SketchOver(500_000, 500_000));

        var result = union.GetResult();

        Assert.True(result.RetainedEntries <= 1024,
            $"Result retained {result.RetainedEntries}, which exceeds k = 1024.");
        Assert.Equal(1_000_000, result.Estimate, 1_000_000 * 0.10);
    }

    [Fact]
    public void UnionCannotExceedTheAccuracyOfItsSmallestInput()
    {
        // A coarse input caps the whole result: values above its theta cannot be
        // known to be absent from it, so the union's theta drops to match.
        var coarse = SketchOver(0, 1_000_000, nominalEntries: 256);
        var fine = SketchOver(1_000_000, 1_000_000, nominalEntries: 65536);

        var union = new ThetaUnion(nominalEntries: 65536);
        union.Union(coarse);
        union.Union(fine);

        var result = union.GetResult();

        Assert.True(result.ThetaLong <= coarse.ThetaLong);
        Assert.Equal(2_000_000, result.Estimate, 2_000_000 * 0.25);
    }

    [Fact]
    public void ResultIsOrderedByDefaultAndCanBeLeftUnordered()
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 100_000));

        var ordered = union.GetResult();
        var unordered = union.GetResult(ordered: false);

        Assert.True(ordered.IsOrdered);
        Assert.False(unordered.IsOrdered);
        Assert.Equal(ordered.RetainedEntries, unordered.RetainedEntries);
        Assert.Equal(
            ordered.HashValues.ToArray(),
            unordered.HashValues.ToArray().OrderBy(h => h).ToArray());
    }

    [Fact]
    public void GetResultIsRepeatableAndNonDestructive()
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 100_000));

        byte[] first = union.GetResult().ToByteArray();
        byte[] second = union.GetResult().ToByteArray();
        Assert.Equal(first, second);

        // And the union keeps working afterwards.
        union.Union(SketchOver(100_000, 100_000));
        Assert.Equal(200_000, union.GetResult().Estimate, 200_000 * Tolerance);
    }

    [Fact]
    public void ResultRoundTripsThroughSerialization()
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 250_000));

        byte[] image = union.GetResult().ToByteArray();

        Assert.Equal(image, CompactThetaSketch.Deserialize(image).ToByteArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(500_000)]
    public void UnionStateRoundTripsThroughSerialization(int n)
    {
        var union = new ThetaUnion();
        if (n > 0)
        {
            union.Union(SketchOver(0, n));
        }

        byte[] image = union.ToByteArray();
        var reread = ThetaUnion.Deserialize(image);

        Assert.Equal((byte)SketchFamily.Union, image[2]);
        Assert.Equal(union.GetResult().ToByteArray(), reread.GetResult().ToByteArray());

        // A resumed union keeps merging correctly.
        reread.Union(SketchOver(1_000_000, 100_000));
        union.Union(SketchOver(1_000_000, 100_000));
        Assert.Equal(union.GetResult().Estimate, reread.GetResult().Estimate);
    }

    [Fact]
    public void ResetReturnsUnionToEmpty()
    {
        var union = new ThetaUnion();
        union.Union(SketchOver(0, 500_000));
        union.Reset();

        Assert.True(union.GetResult().IsEmpty);
        Assert.Equal(TckData.Load("theta_n0_java.sk"), union.GetResult().ToByteArray());
    }

    [Fact]
    public void RejectsSketchesBuiltWithADifferentSeed()
    {
        var other = UpdateThetaSketch.Builder().SetSeed(12345).Build();
        other.Update(1L);

        var union = new ThetaUnion();

        Assert.Throws<InvalidDataException>(() => union.Union(other.Compact()));
    }

    [Fact]
    public void AcceptsUpdateSketchesDirectly()
    {
        // No need to compact first; a union reads the hash table just as well.
        var updatable = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 100_000; i++)
        {
            updatable.Update(i);
        }

        var union = new ThetaUnion();
        union.Union(updatable);

        Assert.Equal(updatable.Compact().Estimate, union.GetResult().Estimate, 100_000 * Tolerance);
    }

    [Fact]
    public void AcceptsAlphaSketches()
    {
        var alpha = UpdateThetaSketch.Builder().SetFamily(SketchFamily.Alpha).Build();
        for (long i = 0; i < 200_000; i++)
        {
            alpha.Update(i);
        }

        var union = new ThetaUnion();
        union.Union(alpha);
        union.Union(SketchOver(200_000, 200_000));

        Assert.Equal(400_000, union.GetResult().Estimate, 400_000 * Tolerance);
    }

    [Fact]
    public void RejectsNonUnionImages()
    {
        Assert.Throws<InvalidDataException>(
            () => ThetaUnion.Deserialize(TckData.Load("theta_n1000000_java.sk")));
    }

    [Fact]
    public void RejectsInvalidNominalEntries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThetaUnion(nominalEntries: 8));
    }
}
