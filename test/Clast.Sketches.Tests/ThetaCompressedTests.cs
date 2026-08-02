using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// The delta-compressed Theta form, serialization version 4.
/// </summary>
public class ThetaCompressedTests
{
    /// <summary>Counts the TCK generated compressed snapshots for.</summary>
    public static IEnumerable<object[]> CompressedCounts()
    {
        foreach (int n in new[] { 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            yield return [n];
        }
    }

    private static CompactThetaSketch Build(int n)
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }
        return sketch.Compact();
    }

    /// <summary>
    /// The strongest check available: the TCK compressed snapshots come from
    /// <c>toByteArrayCompressed()</c> in datasketches-java over the integers
    /// 0..n-1. Matching them byte for byte pins the gap encoding, the entry
    /// width, the count width and the bit packing all at once — a single bit
    /// misplaced anywhere shifts everything after it.
    /// </summary>
    [Theory]
    [MemberData(nameof(CompressedCounts))]
    public void ReproducesJavaCompressedSnapshotsByteForByte(int n)
    {
        byte[] actual = Build(n).ToByteArrayCompressed();

        Assert.Equal(TckData.Load($"theta_compressed_n{n}_java.sk"), actual);
    }

    [Theory]
    [MemberData(nameof(CompressedCounts))]
    public void ReadsJavaCompressedSnapshots(int n)
    {
        byte[] compressed = TckData.Load($"theta_compressed_n{n}_java.sk");
        var fromCompressed = CompactThetaSketch.Deserialize(compressed);
        var fromPlain = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_java.sk"));

        // The two encodings of the same sketch must agree exactly, hash for hash.
        Assert.Equal(fromPlain.ThetaLong, fromCompressed.ThetaLong);
        Assert.Equal(fromPlain.RetainedEntries, fromCompressed.RetainedEntries);
        Assert.Equal(fromPlain.HashValues.ToArray(), fromCompressed.HashValues.ToArray());
        Assert.Equal(fromPlain.Estimate, fromCompressed.Estimate);
    }

    [Theory]
    [MemberData(nameof(CompressedCounts))]
    public void CompressedFormRoundTrips(int n)
    {
        var original = Build(n);

        byte[] compressed = original.ToByteArrayCompressed();
        var reread = CompactThetaSketch.Deserialize(compressed);

        Assert.Equal(original.HashValues.ToArray(), reread.HashValues.ToArray());
        Assert.Equal(original.ThetaLong, reread.ThetaLong);
        Assert.True(reread.IsOrdered);
        Assert.Equal(compressed, reread.ToByteArrayCompressed());
        // And it still converts back to the uncompressed form unchanged.
        Assert.Equal(original.ToByteArray(), reread.ToByteArray());
    }

    [Theory]
    [MemberData(nameof(CompressedCounts))]
    public void CompressionActuallySaves(int n)
    {
        var sketch = Build(n);

        int plain = sketch.ToByteArray().Length;
        int compressed = sketch.ToByteArrayCompressed().Length;

        Assert.True(compressed < plain,
            $"n={n}: compressed {compressed} bytes is not smaller than plain {plain}.");
    }

    [Fact]
    public void CompressionSavesRoughlyAThirdOnALargeSketch()
    {
        var sketch = Build(1_000_000);

        double ratio = (double)sketch.ToByteArrayCompressed().Length / sketch.ToByteArray().Length;

        // Gaps between ordered hashes need far fewer bits than the hashes.
        Assert.InRange(ratio, 0.5, 0.85);
    }

    /// <summary>
    /// Compression does not apply to every sketch, and the fallback is not a
    /// failure — readers distinguish the forms by serialization version, so a
    /// plain image is always a valid answer.
    /// </summary>
    [Fact]
    public void EmptySketchFallsBackToThePlainForm()
    {
        var empty = CompactThetaSketch.Deserialize(TckData.Load("theta_n0_java.sk"));

        Assert.Equal(empty.ToByteArray(), empty.ToByteArrayCompressed());
        Assert.Equal(TckData.Load("theta_n0_java.sk"), empty.ToByteArrayCompressed());
    }

    [Fact]
    public void SingleItemSketchFallsBackToThePlainForm()
    {
        // 16 bytes plain; a compressed preamble alone would be no smaller.
        var single = CompactThetaSketch.Deserialize(TckData.Load("theta_n1_java.sk"));

        Assert.Equal(single.ToByteArray(), single.ToByteArrayCompressed());
    }

    [Fact]
    public void UnorderedSketchFallsBackToThePlainForm()
    {
        // Gaps only shrink if the hashes ascend, so an unordered sketch has
        // nothing to gain.
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 10_000; i++) { sketch.Update(i); }
        var unordered = sketch.Compact(ordered: false);

        Assert.False(unordered.IsOrdered);
        Assert.Equal(unordered.ToByteArray(), unordered.ToByteArrayCompressed());
    }

    [Fact]
    public void ExactSketchOmitsTheta()
    {
        // Below k nothing is sampled away, so theta is 1.0 and need not be stored:
        // one preamble long instead of two.
        var exact = Build(1000);
        byte[] compressed = exact.ToByteArrayCompressed();

        Assert.False(exact.IsEstimationMode);
        Assert.Equal(1, compressed[0]);

        var reread = CompactThetaSketch.Deserialize(compressed);
        Assert.Equal(long.MaxValue, reread.ThetaLong);
        Assert.Equal(exact.HashValues.ToArray(), reread.HashValues.ToArray());
    }

    [Fact]
    public void EstimatingSketchStoresTheta()
    {
        var estimating = Build(1_000_000);
        byte[] compressed = estimating.ToByteArrayCompressed();

        Assert.True(estimating.IsEstimationMode);
        Assert.Equal(2, compressed[0]);
        Assert.Equal(4, compressed[1]);

        Assert.Equal(estimating.ThetaLong, CompactThetaSketch.Deserialize(compressed).ThetaLong);
    }

    [Fact]
    public void CompressedSketchesFeedSetOperations()
    {
        var union = new ThetaUnion();
        union.UnionCompactImage(Build(500_000).ToByteArrayCompressed());
        union.UnionCompactImage(
            CompactThetaSketch.Deserialize(TckData.Load("theta_compressed_n1000000_java.sk")).ToByteArray());

        Assert.Equal(1_000_000, union.GetResult().Estimate, 1_000_000 * 0.03);
    }

    [Fact]
    public void RejectsWrongSeed()
    {
        byte[] image = TckData.Load("theta_compressed_n1000_java.sk");

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image, expectedSeed: 12345));
    }

    [Fact]
    public void RejectsTruncatedImage()
    {
        byte[] image = TckData.Load("theta_compressed_n1000_java.sk");

        Assert.Throws<InvalidDataException>(
            () => CompactThetaSketch.Deserialize(image.AsSpan(0, image.Length - 8)));
    }

    [Theory]
    [InlineData(3, 0)]    // entry width of zero
    [InlineData(3, 64)]   // entry width beyond a long
    [InlineData(4, 0)]    // retained-count width of zero
    [InlineData(4, 5)]    // retained-count width beyond an int
    public void RejectsOutOfRangeWidths(int offset, byte value)
    {
        byte[] image = TckData.Load("theta_compressed_n1000_java.sk");
        image[offset] = value;

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image));
    }

    [Fact]
    public void RejectsInvalidPreambleLength()
    {
        byte[] image = TckData.Load("theta_compressed_n1000_java.sk");
        image[0] = 3;

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image));
    }
}
