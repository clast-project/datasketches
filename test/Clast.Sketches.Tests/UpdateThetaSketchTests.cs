using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

public class UpdateThetaSketchTests
{
    /// <summary>
    /// The strongest compatibility check available: the TCK snapshots were
    /// produced by <c>UpdateSketch.builder().build()</c> in datasketches-java,
    /// fed the integers 0..n-1, then compacted. If the update algorithm, the
    /// hash, the resize schedule, the quick-select cut point and the
    /// serialization all agree, the bytes must agree too — not approximately,
    /// exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(TckData.ThetaCounts), MemberType = typeof(TckData))]
    public void ReproducesJavaSnapshotsByteForByte(int n)
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        byte[] actual = sketch.Compact().ToByteArray();

        Assert.Equal(TckData.Load($"theta_n{n}_java.sk"), actual);
    }

    [Fact]
    public void ReproducesJavaNonEmptyNoEntriesSnapshot()
    {
        // p = 0.01 with a single update whose hash landed above theta.
        var sketch = UpdateThetaSketch.Builder().SetSamplingProbability(0.01f).Build();
        var result = sketch.Update(1L);

        Assert.Equal(ThetaUpdateResult.RejectedOverTheta, result);
        Assert.False(sketch.IsEmpty);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(
            TckData.Load("theta_non_empty_no_entries_java.sk"),
            sketch.Compact().ToByteArray());
    }

    [Fact]
    public void DefaultsMatchTheReferenceImplementation()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(SketchFamily.QuickSelect, sketch.Family);
        Assert.Equal(4096, sketch.NominalEntries);
        Assert.Equal(ThetaSketch.DefaultUpdateSeed, sketch.Seed);
        Assert.Equal(ResizeFactor.X8, sketch.ResizeFactor);
        Assert.Equal(1.0f, sketch.SamplingProbability);
    }

    [Fact]
    public void NewSketchIsEmpty()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.True(sketch.IsEmpty);
        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(0.0, sketch.Estimate);
        Assert.Equal(long.MaxValue, sketch.ThetaLong);
    }

    [Fact]
    public void ReportsDuplicatesAndInserts()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.Inserted, sketch.Update(42L));
        Assert.Equal(ThetaUpdateResult.RejectedDuplicate, sketch.Update(42L));
        Assert.Equal(1, sketch.RetainedEntries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RejectsNullAndEmptyStrings(string? value)
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.RejectedNullOrEmpty, sketch.Update(value));
        Assert.True(sketch.IsEmpty);
    }

    [Fact]
    public void RejectsEmptyByteSpan()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.RejectedNullOrEmpty, sketch.Update(ReadOnlySpan<byte>.Empty));
        Assert.True(sketch.IsEmpty);
    }

    [Fact]
    public void NegativeZeroAndPositiveZeroAreTheSameValue()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.Inserted, sketch.Update(0.0));
        Assert.Equal(ThetaUpdateResult.RejectedDuplicate, sketch.Update(-0.0));
        Assert.Equal(1, sketch.RetainedEntries);
    }

    [Fact]
    public void AllNaNsAreTheSameValue()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.Inserted, sketch.Update(double.NaN));
        Assert.Equal(ThetaUpdateResult.RejectedDuplicate, sketch.Update(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001))));
        Assert.Equal(1, sketch.RetainedEntries);
    }

    [Fact]
    public void StringsHashAsTheirUtf8Bytes()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(ThetaUpdateResult.Inserted, sketch.Update("café"));
        Assert.Equal(
            ThetaUpdateResult.RejectedDuplicate,
            sketch.Update(System.Text.Encoding.UTF8.GetBytes("café")));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void EstimatesAreAccurate(int n)
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        Assert.Equal(n, sketch.Estimate, n * 0.03);
    }

    [Fact]
    public void RebuildTrimsToNominalEntries()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 100_000; i++)
        {
            sketch.Update(i);
        }

        // Between rebuilds the table is allowed to hold more than k; that slack is
        // what keeps the common update path to a single probe.
        int before = sketch.RetainedEntries;
        Assert.True(before > sketch.NominalEntries);

        sketch.Rebuild();

        Assert.True(sketch.RetainedEntries <= sketch.NominalEntries);
        Assert.Equal(100_000, sketch.Estimate, 100_000 * 0.03);
    }

    [Fact]
    public void ResetReturnsSketchToEmpty()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 10_000; i++)
        {
            sketch.Update(i);
        }

        sketch.Reset();

        Assert.True(sketch.IsEmpty);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(long.MaxValue, sketch.ThetaLong);
        Assert.Equal(TckData.Load("theta_n0_java.sk"), sketch.Compact().ToByteArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(100_000)]
    public void UpdateSketchImageRoundTripsThroughCompact(int n)
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        // The update-sketch image stores the raw hash table; deserializing it and
        // compacting must agree with compacting directly.
        byte[] updateImage = sketch.ToByteArray();
        var reread = UpdateThetaSketch.Deserialize(updateImage);

        Assert.Equal(sketch.Family, reread.Family);
        Assert.Equal(sketch.RetainedEntries, reread.RetainedEntries);
        Assert.Equal(sketch.ThetaLong, reread.ThetaLong);
        Assert.Equal(sketch.Compact().ToByteArray(), reread.Compact().ToByteArray());
        Assert.Equal(updateImage, reread.ToByteArray());
    }

    [Fact]
    public void DeserializedUpdateSketchKeepsAccepting()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 1000; i++)
        {
            sketch.Update(i);
        }

        var reread = UpdateThetaSketch.Deserialize(sketch.ToByteArray());
        for (long i = 1000; i < 2000; i++)
        {
            reread.Update(i);
        }

        Assert.Equal(2000, reread.Estimate, 2000 * 0.03);
    }

    [Fact]
    public void CompactCanBeLeftUnordered()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 1000; i++)
        {
            sketch.Update(i);
        }

        var unordered = sketch.Compact(ordered: false);
        var ordered = sketch.Compact(ordered: true);

        Assert.False(unordered.IsOrdered);
        Assert.True(ordered.IsOrdered);
        Assert.Equal(ordered.RetainedEntries, unordered.RetainedEntries);
        Assert.Equal(
            ordered.HashValues.ToArray(),
            unordered.HashValues.ToArray().OrderBy(h => h).ToArray());
    }

    [Fact]
    public void SketchesWithDifferentSeedsProduceDifferentImages()
    {
        var a = UpdateThetaSketch.Builder().Build();
        var b = UpdateThetaSketch.Builder().SetSeed(12345).Build();
        for (long i = 0; i < 100; i++)
        {
            a.Update(i);
            b.Update(i);
        }

        Assert.NotEqual(a.Compact().ToByteArray(), b.Compact().ToByteArray());
        Assert.Throws<InvalidDataException>(() => CompactThetaSketch.Deserialize(b.Compact().ToByteArray()));
    }

    [Fact]
    public void SmallNominalEntriesStillEstimateWell()
    {
        var sketch = UpdateThetaSketch.Builder().SetNominalEntries(16).Build();
        for (long i = 0; i < 10_000; i++)
        {
            sketch.Update(i);
        }

        Assert.Equal(16, sketch.NominalEntries);
        // k = 16 is a very coarse sketch; a wide band is the honest expectation.
        Assert.Equal(10_000, sketch.Estimate, 10_000 * 0.75);
    }

    [Fact]
    public void RejectsInvalidNominalEntries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdateThetaSketch.Builder().SetNominalEntries(8));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdateThetaSketch.Builder().SetNominalEntries(1 << 27));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public void RejectsInvalidSamplingProbability(float p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdateThetaSketch.Builder().SetSamplingProbability(p));
    }

    [Fact]
    public void RejectsNonUpdateFamilies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdateThetaSketch.Builder().SetFamily(SketchFamily.Compact));
    }
}
