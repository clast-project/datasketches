using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// The distribution sort used for retained hashes.
/// </summary>
/// <remarks>
/// It assumes its input is uniformly spread below theta, which the sketch
/// guarantees. These tests cover that case and also the ones that violate it,
/// since a wrong assumption must cost speed and never correctness.
/// </remarks>
public class ThetaSortTests
{
    private static long[] UniformHashes(int count, long thetaLong, int seed)
    {
        var rng = new Random(seed);
        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            // Positive, strictly below theta — the sketch's invariant.
            values[i] = (long)(rng.NextDouble() * (thetaLong - 1)) + 1;
        }
        return values;
    }

    public static IEnumerable<object[]> Counts()
    {
        foreach (int count in new[] { 0, 1, 2, 3, 255, 256, 257, 1000, 4096, 6560, 8192, 100_000 })
        {
            yield return [count];
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void SortsUniformHashes(int count)
    {
        long theta = long.MaxValue / 128;
        long[] values = UniformHashes(count, theta, count);
        long[] expected = (long[])values.Clone();
        Array.Sort(expected);

        ThetaSort.Sort(values, count, theta);

        Assert.Equal(expected, values);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void SortsWhenThetaIsOne(int count)
    {
        // Exact mode: nothing was sampled away, so hashes span the whole range.
        long[] values = UniformHashes(count, long.MaxValue, count + 1);
        long[] expected = (long[])values.Clone();
        Array.Sort(expected);

        ThetaSort.Sort(values, count, long.MaxValue);

        Assert.Equal(expected, values);
    }

    /// <summary>
    /// Every value in one bucket is the worst case for a distribution sort. It
    /// must still sort — the fallback exists so that a violated assumption costs
    /// time, not correctness.
    /// </summary>
    [Fact]
    public void SortsSeverelyClusteredInput()
    {
        const int count = 10_000;
        long theta = long.MaxValue;
        var rng = new Random(99);

        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            // All crammed into a vanishing fraction of the range.
            values[i] = rng.Next(1, 1000);
        }

        long[] expected = (long[])values.Clone();
        Array.Sort(expected);

        ThetaSort.Sort(values, count, theta);

        Assert.Equal(expected, values);
    }

    [Fact]
    public void SortsDuplicates()
    {
        const int count = 5000;
        long theta = long.MaxValue / 4;
        var rng = new Random(7);

        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            // Draw from a small pool so most values repeat many times.
            values[i] = (long)(rng.Next(1, 50) * (theta / 60.0));
        }

        long[] expected = (long[])values.Clone();
        Array.Sort(expected);

        ThetaSort.Sort(values, count, theta);

        Assert.Equal(expected, values);
    }

    [Fact]
    public void SortsAlreadySortedAndReversedInput()
    {
        const int count = 4096;
        long theta = long.MaxValue / 2;

        long[] ascending = UniformHashes(count, theta, 1);
        Array.Sort(ascending);
        long[] expected = (long[])ascending.Clone();

        long[] sortedInput = (long[])ascending.Clone();
        ThetaSort.Sort(sortedInput, count, theta);
        Assert.Equal(expected, sortedInput);

        long[] reversed = (long[])ascending.Clone();
        Array.Reverse(reversed);
        ThetaSort.Sort(reversed, count, theta);
        Assert.Equal(expected, reversed);
    }

    [Fact]
    public void SortsValuesAtTheExtremesOfTheRange()
    {
        long theta = long.MaxValue;
        long[] values = [theta - 1, 1, theta / 2, 2, theta - 2, long.MaxValue / 3];
        long[] expected = (long[])values.Clone();
        Array.Sort(expected);

        // Below the distribution-sort threshold, so this also covers the
        // small-input path.
        ThetaSort.Sort(values, values.Length, theta);

        Assert.Equal(expected, values);
    }

    [Fact]
    public void SortsOnlyThePrefixItIsGiven()
    {
        long theta = long.MaxValue / 8;
        long[] values = UniformHashes(1000, theta, 3);
        long[] tail = values.Skip(500).ToArray();

        long[] expectedHead = values.Take(500).ToArray();
        Array.Sort(expectedHead);

        ThetaSort.Sort(values, 500, theta);

        Assert.Equal(expectedHead, values.Take(500));
        // Everything past the count must be untouched.
        Assert.Equal(tail, values.Skip(500));
    }
}
