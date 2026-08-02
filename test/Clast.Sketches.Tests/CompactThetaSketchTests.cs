using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// Read/write compatibility for compact Theta sketches, checked against the
/// apache/datasketches-tck snapshots produced by the reference Java and C++
/// implementations.
/// </summary>
public class CompactThetaSketchTests
{
    // The generators fed each sketch the integers 0..n-1, so the true distinct
    // count is n. 3% is the tolerance the reference cross-language tests use for
    // a sketch built with the default 4096 nominal entries.
    private const double EstimateTolerance = 0.03;

    [Theory]
    [MemberData(nameof(TckData.ThetaCounts), MemberType = typeof(TckData))]
    public void ReadsJavaSnapshots(int n)
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_java.sk"));

        AssertWellFormed(sketch, n);
    }

    [Theory]
    [MemberData(nameof(TckData.ThetaCounts), MemberType = typeof(TckData))]
    public void ReadsCppSnapshots(int n)
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_cpp.sk"));

        AssertWellFormed(sketch, n);
    }

    [Theory]
    [MemberData(nameof(TckData.ThetaCounts), MemberType = typeof(TckData))]
    public void RoundTripsJavaSnapshotsByteForByte(int n)
    {
        byte[] original = TckData.Load($"theta_n{n}_java.sk");

        byte[] rewritten = CompactThetaSketch.Deserialize(original).ToByteArray();

        Assert.Equal(original, rewritten);
    }

    [Theory]
    [MemberData(nameof(TckData.ThetaCounts), MemberType = typeof(TckData))]
    public void ReadsJavaAndCppSnapshotsToTheSameSketch(int n)
    {
        var fromJava = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_java.sk"));
        var fromCpp = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_cpp.sk"));

        Assert.Equal(fromJava.IsEmpty, fromCpp.IsEmpty);
        Assert.Equal(fromJava.ThetaLong, fromCpp.ThetaLong);
        Assert.Equal(fromJava.RetainedEntries, fromCpp.RetainedEntries);
        Assert.Equal(fromJava.HashValues.ToArray(), fromCpp.HashValues.ToArray());
    }

    /// <summary>
    /// The two reference implementations do not agree byte-for-byte on the two
    /// degenerate sketches, and both forms are valid. Java omits the seed hash
    /// from an empty image (there are no hashes to be incompatible about) while
    /// C++ writes it; and Java sets the single-item flag while C++ leaves it
    /// clear, which is why single items are detected by shape rather than by
    /// that flag. Anything that reads these must accept both.
    /// </summary>
    [Theory]
    [InlineData(0, 0x1E, 0x0000, 0x1E, 0x93CC)]
    [InlineData(1, 0x3A, 0x93CC, 0x1A, 0x93CC)]
    public void JavaAndCppDisagreeOnDegenerateImages(
        int n, int javaFlags, int javaSeedHash, int cppFlags, int cppSeedHash)
    {
        byte[] java = TckData.Load($"theta_n{n}_java.sk");
        byte[] cpp = TckData.Load($"theta_n{n}_cpp.sk");

        Assert.NotEqual(java, cpp);
        Assert.Equal(javaFlags, java[5]);
        Assert.Equal(cppFlags, cpp[5]);
        Assert.Equal(javaSeedHash, java[6] | (java[7] << 8));
        Assert.Equal(cppSeedHash, cpp[6] | (cpp[7] << 8));

        // We emit the Java form for both, so the C++ image does not survive a
        // round trip byte-for-byte — but it does survive semantically.
        Assert.Equal(java, CompactThetaSketch.Deserialize(cpp).ToByteArray());
    }

    [Fact]
    public void ReadsNonEmptySketchWithNoRetainedEntries()
    {
        // Built with p = 0.01 and a single update that fell above theta: not
        // empty, estimating, yet retaining nothing.
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_non_empty_no_entries_java.sk"));

        Assert.False(sketch.IsEmpty);
        Assert.True(sketch.IsEstimationMode);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(0.0, sketch.Estimate);
        Assert.Equal(0.01, sketch.Theta, 3);
    }

    [Fact]
    public void EmptySketchHasCanonicalEightByteImage()
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_n0_java.sk"));

        Assert.True(sketch.IsEmpty);
        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(0.0, sketch.Estimate);
        Assert.Equal(1.0, sketch.Theta);
        Assert.Equal(new byte[] { 1, 3, 3, 0, 0, 0x1E, 0, 0 }, sketch.ToByteArray());
    }

    [Fact]
    public void SingleItemSketchIsExactAndSixteenBytes()
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_n1_java.sk"));

        Assert.False(sketch.IsEmpty);
        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(1, sketch.RetainedEntries);
        Assert.Equal(1.0, sketch.Estimate);
        Assert.Equal(16, sketch.SerializedSizeBytes);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void SmallCountsAreExact(int n)
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_java.sk"));

        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(long.MaxValue, sketch.ThetaLong);
        Assert.Equal(n, sketch.RetainedEntries);
        Assert.Equal(n, sketch.Estimate);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void LargeCountsSwitchToEstimation(int n)
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load($"theta_n{n}_java.sk"));

        Assert.True(sketch.IsEstimationMode);
        Assert.True(sketch.ThetaLong < long.MaxValue);
        Assert.True(sketch.RetainedEntries < n);
        Assert.Equal(n, sketch.Estimate, n * EstimateTolerance);
    }

    [Fact]
    public void SerializeWritesIntoCallerBuffer()
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_n100_java.sk"));

        byte[] buffer = new byte[sketch.SerializedSizeBytes + 8];
        int written = sketch.Serialize(buffer);

        Assert.Equal(sketch.SerializedSizeBytes, written);
        Assert.Equal(sketch.ToByteArray(), buffer.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void SerializeRejectsUndersizedBuffer()
    {
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_n100_java.sk"));

        Assert.Throws<ArgumentException>(() => sketch.Serialize(new byte[sketch.SerializedSizeBytes - 1]));
    }

    [Fact]
    public void RejectsWrongSeed()
    {
        byte[] image = TckData.Load("theta_n100_java.sk");

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image, expectedSeed: 12345));
    }

    [Fact]
    public void AcceptsEmptySketchUnderAnySeed()
    {
        // An empty image carries no hashes, so there is nothing a seed mismatch
        // could corrupt; the reference implementations skip the check too.
        var sketch = CompactThetaSketch.Deserialize(TckData.Load("theta_n0_java.sk"), expectedSeed: 12345);

        Assert.True(sketch.IsEmpty);
    }

    [Fact]
    public void RejectsTruncatedImage()
    {
        byte[] image = TckData.Load("theta_n100_java.sk");

        Assert.Throws<InvalidDataException>(
            () => CompactThetaSketch.Deserialize(image.AsSpan(0, image.Length - 8)));
    }

    [Fact]
    public void RejectsNonCompactFamily()
    {
        byte[] image = TckData.Load("theta_n100_java.sk");
        image[2] = (byte)SketchFamily.QuickSelect;

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image));
    }

    [Fact]
    public void RejectsUnknownSerializationVersion()
    {
        byte[] image = TckData.Load("theta_n100_java.sk");
        image[1] = 9;

        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(image));
    }

    [Fact]
    public void ReportsCompressedFormAsUnsupported()
    {
        byte[] image = TckData.Load("theta_n100_java.sk");
        image[1] = 4;

        Assert.Throws<NotSupportedException>(() => CompactThetaSketch.Deserialize(image));
    }

    private static void AssertWellFormed(CompactThetaSketch sketch, int n)
    {
        Assert.Equal(n == 0, sketch.IsEmpty);
        Assert.True(sketch.IsOrdered);
        Assert.Equal(n, sketch.Estimate, Math.Max(n * EstimateTolerance, 1e-9));

        // Every retained hash must be a positive value below theta, and the
        // ordered flag has to actually mean something.
        long previous = 0;
        foreach (long hash in sketch.HashValues)
        {
            Assert.True(hash > previous, $"Hashes are not ascending at value {hash}.");
            Assert.True(hash < sketch.ThetaLong, $"Hash {hash} is not below theta {sketch.ThetaLong}.");
            previous = hash;
        }
    }
}
