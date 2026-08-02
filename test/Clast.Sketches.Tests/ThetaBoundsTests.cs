using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// Confidence bounds as exposed on the sketches themselves.
/// </summary>
public class ThetaBoundsTests
{
    private static UpdateThetaSketch Build(
        long start, int count, SketchFamily family = SketchFamily.QuickSelect, int nominalEntries = 4096)
    {
        var sketch = UpdateThetaSketch.Builder()
            .SetFamily(family)
            .SetNominalEntries(nominalEntries)
            .Build();
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch;
    }

    [Fact]
    public void EmptySketchHasZeroBounds()
    {
        var sketch = UpdateThetaSketch.Builder().Build();

        Assert.Equal(0.0, sketch.GetLowerBound());
        Assert.Equal(0.0, sketch.GetUpperBound());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void ExactSketchHasBoundsEqualToItsCount(int n)
    {
        // Below k nothing is sampled away, so there is no uncertainty to report.
        var sketch = Build(0, n);

        Assert.False(sketch.IsEstimationMode);
        Assert.Equal(n, sketch.GetLowerBound());
        Assert.Equal(n, sketch.GetUpperBound());
        Assert.Equal(n, sketch.Estimate);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void EstimatingSketchBracketsItsEstimate(int n)
    {
        var sketch = Build(0, n);

        Assert.True(sketch.IsEstimationMode);
        Assert.True(sketch.GetLowerBound() <= sketch.Estimate);
        Assert.True(sketch.GetUpperBound() >= sketch.Estimate);
        Assert.True(sketch.GetLowerBound() >= 0.0);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void BoundsContainTheTrueCount(int n)
    {
        var sketch = Build(0, n);

        Assert.InRange(n, sketch.GetLowerBound(3), sketch.GetUpperBound(3));
    }

    [Fact]
    public void WiderConfidenceGivesWiderBounds()
    {
        var sketch = Build(0, 1_000_000);

        Assert.True(sketch.GetLowerBound(1) >= sketch.GetLowerBound(2));
        Assert.True(sketch.GetLowerBound(2) >= sketch.GetLowerBound(3));
        Assert.True(sketch.GetUpperBound(1) <= sketch.GetUpperBound(2));
        Assert.True(sketch.GetUpperBound(2) <= sketch.GetUpperBound(3));
    }

    [Fact]
    public void DefaultConfidenceIsTwoStandardDeviations()
    {
        var sketch = Build(0, 1_000_000);

        Assert.Equal(sketch.GetLowerBound(2), sketch.GetLowerBound());
        Assert.Equal(sketch.GetUpperBound(2), sketch.GetUpperBound());
    }

    /// <summary>
    /// The bounds' actual claim: over many independent sketches, roughly 95% of
    /// two-standard-deviation intervals should contain the truth. Checked
    /// loosely — well above chance, and not so tight that a legitimate run
    /// fails. Deterministic inputs, so this either always passes or always
    /// fails.
    /// </summary>
    [Theory]
    [InlineData(SketchFamily.QuickSelect)]
    [InlineData(SketchFamily.Alpha)]
    public void TwoSigmaBoundsCoverTheTruthAboutNineteenTimesInTwenty(SketchFamily family)
    {
        const int trials = 60;
        const int n = 500_000;
        int covered = 0;

        for (int trial = 0; trial < trials; trial++)
        {
            var sketch = Build(trial * 10_000_000L, n, family, nominalEntries: 512);
            if (sketch.GetLowerBound(2) <= n && n <= sketch.GetUpperBound(2))
            {
                covered++;
            }
        }

        Assert.True(
            covered >= trials * 0.85,
            $"{family}: only {covered}/{trials} two-sigma intervals covered the true count.");
    }

    [Fact]
    public void ThreeSigmaBoundsCoverTheTruthEveryTime()
    {
        const int trials = 60;
        const int n = 500_000;

        for (int trial = 0; trial < trials; trial++)
        {
            var sketch = Build(trial * 10_000_000L, n, nominalEntries: 512);
            Assert.InRange(n, sketch.GetLowerBound(3), sketch.GetUpperBound(3));
        }
    }

    [Fact]
    public void CompactSketchReportsTheSameBoundsAsItsSource()
    {
        var sketch = Build(0, 1_000_000);
        var compact = sketch.Compact();

        // Compacting is lossless, so the uncertainty is unchanged.
        Assert.Equal(sketch.GetLowerBound(2), compact.GetLowerBound(2));
        Assert.Equal(sketch.GetUpperBound(2), compact.GetUpperBound(2));
    }

    [Fact]
    public void BoundsSurviveSerialization()
    {
        var compact = Build(0, 1_000_000).Compact();
        var reread = CompactThetaSketch.Deserialize(compact.ToByteArray());

        Assert.Equal(compact.GetLowerBound(2), reread.GetLowerBound(2));
        Assert.Equal(compact.GetUpperBound(2), reread.GetUpperBound(2));
    }

    [Fact]
    public void UnionResultReportsBounds()
    {
        var union = new ThetaUnion();
        union.Union(Build(0, 500_000).Compact());
        union.Union(Build(500_000, 500_000).Compact());

        var result = union.GetResult();

        Assert.InRange(1_000_000, result.GetLowerBound(3), result.GetUpperBound(3));
    }

    /// <summary>
    /// Alpha uses an HIP variance rather than the binomial interval, because its
    /// retained set is not a plain uniform sample — theta decayed continuously
    /// as the sketch was built.
    /// </summary>
    [Fact]
    public void AlphaBoundsUseItsOwnEstimator()
    {
        var alpha = Build(0, 1_000_000, SketchFamily.Alpha);
        var quickSelect = Build(0, 1_000_000);

        Assert.InRange(1_000_000, alpha.GetLowerBound(3), alpha.GetUpperBound(3));

        // Different estimators, so the intervals should not coincide.
        Assert.NotEqual(alpha.GetUpperBound(2) - alpha.GetLowerBound(2),
                        quickSelect.GetUpperBound(2) - quickSelect.GetLowerBound(2));
    }

    [Fact]
    public void AlphaExactSketchHasBoundsEqualToItsCount()
    {
        var alpha = Build(0, 100, SketchFamily.Alpha);

        Assert.False(alpha.IsEstimationMode);
        Assert.Equal(100, alpha.GetLowerBound());
        Assert.Equal(100, alpha.GetUpperBound());
    }

    [Fact]
    public void AlphaBoundsBracketItsEstimate()
    {
        var alpha = Build(0, 1_000_000, SketchFamily.Alpha);

        Assert.True(alpha.GetLowerBound(2) <= alpha.Estimate);
        Assert.True(alpha.GetUpperBound(2) >= alpha.Estimate);
        Assert.True(alpha.GetLowerBound(2) >= 0.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsInvalidConfidenceLevel(int numStdDev)
    {
        var sketch = Build(0, 1_000_000);
        var alpha = Build(0, 1_000_000, SketchFamily.Alpha);
        var compact = sketch.Compact();

        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.GetLowerBound(numStdDev));
        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.GetUpperBound(numStdDev));
        Assert.Throws<ArgumentOutOfRangeException>(() => alpha.GetLowerBound(numStdDev));
        Assert.Throws<ArgumentOutOfRangeException>(() => alpha.GetUpperBound(numStdDev));
        Assert.Throws<ArgumentOutOfRangeException>(() => compact.GetLowerBound(numStdDev));
    }

    [Fact]
    public void RejectsInvalidConfidenceLevelEvenWhenExact()
    {
        // The reference only validates on the estimating path, which lets a
        // meaningless argument pass silently. We always validate.
        var exact = Build(0, 10);

        Assert.False(exact.IsEstimationMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => exact.GetLowerBound(0));
    }
}
