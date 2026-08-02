using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// The Alpha update sketch. The TCK has no Alpha snapshots — its generators use
/// the default QuickSelect family — so these check the algorithm's invariants
/// and accuracy rather than exact bytes. Every case is deterministic (fixed
/// inputs, fixed seed), so the tolerances below are regression bounds, not
/// statistical claims.
/// </summary>
public class AlphaThetaSketchTests
{
    private static UpdateThetaSketch NewAlpha(int nominalEntries = 4096) =>
        UpdateThetaSketch.Builder()
            .SetFamily(SketchFamily.Alpha)
            .SetNominalEntries(nominalEntries)
            .Build();

    [Fact]
    public void ReportsAlphaFamily()
    {
        var sketch = NewAlpha();

        Assert.Equal(SketchFamily.Alpha, sketch.Family);
        Assert.Equal((byte)SketchFamily.Alpha, sketch.ToByteArray()[2]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    public void RequiresAtLeastFiveHundredTwelveNominalEntries(int nominalEntries)
    {
        // Alpha's theta decay is only well-behaved for a large enough k.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdateThetaSketch.Builder()
                .SetFamily(SketchFamily.Alpha)
                .SetNominalEntries(nominalEntries)
                .Build());
    }

    [Fact]
    public void AcceptsExactlyFiveHundredTwelveNominalEntries()
    {
        var sketch = NewAlpha(512);

        Assert.Equal(512, sketch.NominalEntries);
    }

    [Fact]
    public void NewSketchIsEmpty()
    {
        var sketch = NewAlpha();

        Assert.True(sketch.IsEmpty);
        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(0.0, sketch.Estimate);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void EstimatesAreAccurate(int n)
    {
        var sketch = NewAlpha();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        Assert.Equal(n, sketch.Estimate, n * 0.03);
    }

    [Fact]
    public void SmallCountsAreExact()
    {
        var sketch = NewAlpha();
        for (long i = 0; i < 100; i++)
        {
            sketch.Update(i);
        }

        // Below k the sketch has not decayed theta, so it is still counting exactly.
        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(100, sketch.RetainedEntries);
        Assert.Equal(100.0, sketch.Estimate);
    }

    [Fact]
    public void SwitchesToEstimationPastNominalEntries()
    {
        var sketch = NewAlpha(512);
        for (long i = 0; i < 5000; i++)
        {
            sketch.Update(i);
        }

        Assert.True(sketch.IsEstimationMode);
        Assert.True(sketch.ThetaLong < long.MaxValue);
    }

    [Fact]
    public void RetainedCountStaysConsistentWhileDirty()
    {
        // Alpha decays theta on every insert past k, which can strand entries
        // above theta without touching them. The retained count must reflect
        // that, and compacting must drop exactly those entries.
        var sketch = NewAlpha(512);
        for (long i = 0; i < 20_000; i++)
        {
            sketch.Update(i);
        }

        var compact = sketch.Compact();

        Assert.Equal(sketch.RetainedEntries, compact.RetainedEntries);
        foreach (long hash in compact.HashValues)
        {
            Assert.True(hash > 0 && hash < compact.ThetaLong);
        }
    }

    [Fact]
    public void RebuildIsIdempotentForEstimates()
    {
        var sketch = NewAlpha(512);
        for (long i = 0; i < 20_000; i++)
        {
            sketch.Update(i);
        }

        double before = sketch.Estimate;
        int retainedBefore = sketch.RetainedEntries;
        sketch.Rebuild();

        // Sweeping stale entries changes neither theta nor the valid count, so
        // the estimate is unchanged.
        Assert.Equal(before, sketch.Estimate);
        Assert.Equal(retainedBefore, sketch.RetainedEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(100_000)]
    public void CompactFormIsReadableAsAStandardThetaSketch(int n)
    {
        var sketch = NewAlpha();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        byte[] image = sketch.Compact().ToByteArray();
        var reread = CompactThetaSketch.Deserialize(image);

        // Compacting erases the family distinction: an Alpha sketch and a
        // QuickSelect sketch both serialize as Compact, which is what makes the
        // Puffin blob format family-agnostic on read.
        Assert.Equal(SketchFamily.Compact, reread.Family);
        Assert.Equal(sketch.RetainedEntries, reread.RetainedEntries);
        Assert.Equal(sketch.Compact().Estimate, reread.Estimate);
        Assert.Equal(image, reread.ToByteArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(100_000)]
    public void UpdateImageRoundTrips(int n)
    {
        var sketch = NewAlpha();
        for (long i = 0; i < n; i++)
        {
            sketch.Update(i);
        }

        byte[] image = sketch.ToByteArray();
        var reread = UpdateThetaSketch.Deserialize(image);

        Assert.Equal(SketchFamily.Alpha, reread.Family);
        Assert.Equal(sketch.RetainedEntries, reread.RetainedEntries);
        Assert.Equal(sketch.ThetaLong, reread.ThetaLong);
        Assert.Equal(image, reread.ToByteArray());
    }

    [Fact]
    public void ResetReturnsSketchToEmpty()
    {
        var sketch = NewAlpha();
        for (long i = 0; i < 50_000; i++)
        {
            sketch.Update(i);
        }

        sketch.Reset();

        Assert.True(sketch.IsEmpty);
        Assert.Equal(0, sketch.RetainedEntries);
        Assert.Equal(long.MaxValue, sketch.ThetaLong);
    }

    [Fact]
    public void DuplicatesDoNotInflateTheEstimate()
    {
        var sketch = NewAlpha();
        for (int pass = 0; pass < 3; pass++)
        {
            for (long i = 0; i < 50_000; i++)
            {
                sketch.Update(i);
            }
        }

        Assert.Equal(50_000, sketch.Estimate, 50_000 * 0.03);
    }

    [Fact]
    public void IsAtLeastAsAccurateAsQuickSelectAcrossTrials()
    {
        // Alpha's continuous theta decay is supposed to buy accuracy over
        // QuickSelect's batched cut. Deterministic trials over disjoint input
        // ranges, comparing total absolute error.
        const int n = 200_000;
        double alphaError = 0;
        double quickSelectError = 0;

        for (int trial = 0; trial < 8; trial++)
        {
            long offset = (long)trial * 10_000_000L;
            var alpha = NewAlpha();
            var quickSelect = UpdateThetaSketch.Builder().Build();

            for (long i = 0; i < n; i++)
            {
                alpha.Update(offset + i);
                quickSelect.Update(offset + i);
            }

            alphaError += Math.Abs(alpha.Estimate - n);
            quickSelectError += Math.Abs(quickSelect.Estimate - n);
        }

        Assert.True(
            alphaError <= quickSelectError,
            $"Alpha total error {alphaError:F0} should not exceed QuickSelect's {quickSelectError:F0}.");
    }
}
