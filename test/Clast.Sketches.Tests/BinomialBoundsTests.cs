using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// Parity check for the binomial confidence bounds, ported from
/// <c>BinomialBoundsNTest</c> in apache/datasketches-java.
/// </summary>
/// <remarks>
/// The sweep walks a grid of sample counts and sampling rates, accumulating the
/// log of every bound it computes, and compares the totals against sums the
/// reference implementation produces. Logs are used so a discrepancy in a small
/// bound is not swamped by the large ones. Matching to 1e-15 relative across
/// hundreds of thousands of evaluations pins every branch and every table entry
/// — there is nowhere for a wrong value to hide.
/// </remarks>
public class BinomialBoundsTests
{
    private const double Tolerance = 1e-15;

    /// <summary>
    /// Reference sums from datasketches-java, one row per grid and confidence
    /// level: {sum of log lower bounds, log upper bounds, log lower bounds at
    /// 1-p, log upper bounds at 1-p, evaluation count}.
    /// </summary>
    /// <remarks>
    /// All three grids are enabled, including the largest, which the reference
    /// test disables for runtime. Together they are about 38 million bound
    /// evaluations and still finish in under a second.
    /// </remarks>
    public static IEnumerable<object[]> ReferenceSums()
    {
        // maxSamples = 20, minP = 1e-3
        yield return [20, 1e-3, 1, 7.083330682531043e+04, 8.530373642825481e+04, 3.273647725073409e+04, 3.734024243699785e+04, 57750L];
        yield return [20, 1e-3, 2, 6.539415269641498e+04, 8.945522372568645e+04, 3.222302546497840e+04, 3.904738469737429e+04, 57750L];
        yield return [20, 1e-3, 3, 6.006043493107306e+04, 9.318105731423477e+04, 3.186269956585285e+04, 4.096466221922520e+04, 57750L];

        // maxSamples = 200, minP = 1e-5
        yield return [200, 1e-5, 1, 2.275584770163813e+06, 2.347586549014998e+06, 1.020399409477305e+06, 1.036729927598294e+06, 920982L];
        yield return [200, 1e-5, 2, 2.243569126699713e+06, 2.374663344107342e+06, 1.017017233582122e+06, 1.042597845553438e+06, 920982L];
        yield return [200, 1e-5, 3, 2.210056231903739e+06, 2.400441267999687e+06, 1.014081235946986e+06, 1.049480769755676e+06, 920982L];

        // maxSamples = 2000, minP = 1e-7
        yield return [2000, 1e-7, 1, 4.688240115809608e+07, 4.718067204619278e+07, 2.148362024482338e+07, 2.153118905212302e+07, 12834414L];
        yield return [2000, 1e-7, 2, 4.674205938540214e+07, 4.731333757486791e+07, 2.146902141966406e+07, 2.154916650733873e+07, 12834414L];
        yield return [2000, 1e-7, 3, 4.659896614422579e+07, 4.744404182094614e+07, 2.145525391547799e+07, 2.156815612325058e+07, 12834414L];
    }

    [Theory]
    [MemberData(nameof(ReferenceSums))]
    public void MatchesJavaReferenceAcrossTheGrid(
        int maxSamples,
        double minP,
        int numStdDev,
        double expectedLowerSum,
        double expectedUpperSum,
        double expectedComplementLowerSum,
        double expectedComplementUpperSum,
        long expectedCount)
    {
        double lowerSum = 0.0, upperSum = 0.0, complementLowerSum = 0.0, complementUpperSum = 0.0;
        long count = 0;

        for (long numSamples = 0; numSamples <= maxSamples;
             numSamples = Math.Max(numSamples + 1, (1001 * numSamples) / 1000))
        {
            for (double p = 1.0; p >= minP; p *= 0.99)
            {
                lowerSum += Math.Log(BinomialBounds.LowerBound((int)numSamples, p, numStdDev, false) + 1.0);
                upperSum += Math.Log(BinomialBounds.UpperBound((int)numSamples, p, numStdDev, false) + 1.0);
                count += 2;

                if (p < 1.0)
                {
                    // Also sweep the complementary rate, which reaches the
                    // near-one branches the forward sweep barely touches.
                    complementLowerSum += Math.Log(BinomialBounds.LowerBound((int)numSamples, 1.0 - p, numStdDev, false) + 1.0);
                    complementUpperSum += Math.Log(BinomialBounds.UpperBound((int)numSamples, 1.0 - p, numStdDev, false) + 1.0);
                    count += 2;
                }
            }
        }

        Assert.Equal(expectedCount, count);
        AssertRelative(expectedLowerSum, lowerSum, nameof(lowerSum));
        AssertRelative(expectedUpperSum, upperSum, nameof(upperSum));
        AssertRelative(expectedComplementLowerSum, complementLowerSum, nameof(complementLowerSum));
        AssertRelative(expectedComplementUpperSum, complementUpperSum, nameof(complementUpperSum));
    }

    private static void AssertRelative(double expected, double actual, string what)
    {
        double relative = Math.Abs((actual / expected) - 1.0);
        Assert.True(
            relative < Tolerance,
            $"{what}: expected {expected:e15}, got {actual:e15}, relative difference {relative:e3}.");
    }

    [Fact]
    public void BoundsBracketTheEstimate()
    {
        const double theta = 0.001;
        for (int samples = 0; samples <= 500; samples++)
        {
            double estimate = samples / theta;
            double lower = BinomialBounds.LowerBound(samples, theta, 2, false);
            double upper = BinomialBounds.UpperBound(samples, theta, 2, false);

            Assert.True(lower <= estimate, $"Lower {lower} exceeded estimate {estimate} at {samples} samples.");
            Assert.True(upper >= estimate, $"Upper {upper} below estimate {estimate} at {samples} samples.");
            Assert.True(lower >= 0.0);
        }
    }

    [Fact]
    public void WiderConfidenceGivesWiderBounds()
    {
        const double theta = 0.01;
        const int samples = 500;

        double previousWidth = -1.0;
        for (int numStdDev = 1; numStdDev <= 3; numStdDev++)
        {
            double width = BinomialBounds.UpperBound(samples, theta, numStdDev, false)
                - BinomialBounds.LowerBound(samples, theta, numStdDev, false);
            Assert.True(width > previousWidth, $"Width did not grow at {numStdDev} standard deviations.");
            previousWidth = width;
        }
    }

    [Fact]
    public void ExactSamplingHasNoUncertainty()
    {
        // Theta of 1.0 means nothing was sampled away, so the count is the count.
        Assert.Equal(100.0, BinomialBounds.LowerBound(100, 1.0, 2, false));
        Assert.Equal(100.0, BinomialBounds.UpperBound(100, 1.0, 2, false));
    }

    [Fact]
    public void NoDataSeenGivesZeroBounds()
    {
        Assert.Equal(0.0, BinomialBounds.LowerBound(0, 0.001, 2, noDataSeen: true));
        Assert.Equal(0.0, BinomialBounds.UpperBound(0, 0.001, 2, noDataSeen: true));
    }

    [Fact]
    public void RetainingNothingStillAdmitsAnUpperBound()
    {
        // Data was presented and all of it sampled away: the count is not
        // provably zero, so the upper bound must be positive.
        Assert.Equal(0.0, BinomialBounds.LowerBound(0, 0.001, 2, noDataSeen: false));
        Assert.True(BinomialBounds.UpperBound(0, 0.001, 2, noDataSeen: false) > 0.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void RejectsInvalidConfidenceLevel(int numStdDev)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BinomialBounds.LowerBound(10, 0.5, numStdDev, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BinomialBounds.UpperBound(10, 0.5, numStdDev, false));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void RejectsInvalidTheta(double theta)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BinomialBounds.LowerBound(10, theta, 2, false));
    }

    [Fact]
    public void RejectsNegativeSampleCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BinomialBounds.LowerBound(-1, 0.5, 2, false));
    }
}
