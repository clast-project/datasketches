using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// Intersection and set difference.
/// </summary>
/// <remarks>
/// Where the operands are in exact mode the true answer is known, so those
/// cases assert it exactly rather than within a tolerance â€” including the
/// retained hashes themselves, which pins the result set and not just its size.
/// </remarks>
public class ThetaSetOperationTests
{
    private const double Tolerance = 0.03;

    /// <summary>
    /// Set-operation results carry wider relative error than their operands. An
    /// intersection of two k = 4096 sketches retains only about half that many
    /// entries, so its error is roughly 1/sqrt(2048) — over 2% at one standard
    /// deviation. Estimate assertions here are loose on purpose; the tight check
    /// is that the true value falls inside the result's own bounds.
    /// </summary>
    private const double SetOperationTolerance = 0.08;

    private static CompactThetaSketch Range(long start, long count, int nominalEntries = 4096)
    {
        var sketch = UpdateThetaSketch.Builder().SetNominalEntries(nominalEntries).Build();
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch.Compact();
    }

    private static long[] SortedHashes(CompactThetaSketch sketch) => sketch.HashValues.ToArray();

    // ---------- Intersection ----------

    [Fact]
    public void IntersectionOfExactOverlapIsTheOverlapExactly()
    {
        // 0..999 and 500..1499 share exactly 500..999.
        var result = ThetaIntersection.Of(Range(0, 1000), Range(500, 1000));

        Assert.False(result.IsEstimationMode);
        Assert.Equal(500, result.RetainedEntries);
        Assert.Equal(500.0, result.Estimate);
        Assert.Equal(SortedHashes(Range(500, 500)), SortedHashes(result));
    }

    [Fact]
    public void IntersectionOfDisjointExactSketchesIsEmpty()
    {
        var result = ThetaIntersection.Of(Range(0, 1000), Range(1000, 1000));

        Assert.Equal(0, result.RetainedEntries);
        Assert.Equal(0.0, result.Estimate);
        // Disjoint with no sampling in play means provably empty, not merely
        // estimated as zero.
        Assert.True(result.IsEmpty);
        Assert.Equal(TckData.Load("theta_n0_java.sk"), result.ToByteArray());
    }

    [Fact]
    public void IntersectionWithItselfIsItself()
    {
        var sketch = Range(0, 1000);

        Assert.Equal(sketch.ToByteArray(), ThetaIntersection.Of(sketch, sketch).ToByteArray());
    }

    [Fact]
    public void IntersectionWithEmptyIsEmpty()
    {
        var result = ThetaIntersection.Of(Range(0, 1000), Range(0, 0));

        Assert.True(result.IsEmpty);
        Assert.Equal(0.0, result.Estimate);
    }

    [Fact]
    public void IntersectionOnceEmptyStaysEmpty()
    {
        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 1000));
        intersection.Intersect(Range(0, 0));
        intersection.Intersect(Range(0, 1000));

        Assert.True(intersection.GetResult().IsEmpty);
    }

    [Fact]
    public void IntersectionOfOneSketchIsThatSketch()
    {
        var sketch = Range(0, 1000);
        var intersection = new ThetaIntersection();
        intersection.Intersect(sketch);

        Assert.Equal(sketch.ToByteArray(), intersection.GetResult().ToByteArray());
    }

    [Fact]
    public void IntersectionOfSeveralNarrowsCorrectly()
    {
        // 0..999, 500..1499, 750..1749 share exactly 750..999.
        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 1000));
        intersection.Intersect(Range(500, 1000));
        intersection.Intersect(Range(750, 1000));

        Assert.Equal(SortedHashes(Range(750, 250)), SortedHashes(intersection.GetResult()));
    }

    [Fact]
    public void IntersectionIsOrderIndependent()
    {
        var a = Range(0, 1000);
        var b = Range(500, 1000);
        var c = Range(750, 1000);

        var forward = new ThetaIntersection();
        forward.Intersect(a);
        forward.Intersect(b);
        forward.Intersect(c);

        var backward = new ThetaIntersection();
        backward.Intersect(c);
        backward.Intersect(b);
        backward.Intersect(a);

        Assert.Equal(forward.GetResult().ToByteArray(), backward.GetResult().ToByteArray());
    }

    [Fact]
    public void IntersectionEstimatesLargeOverlaps()
    {
        // 0..999,999 and 500,000..1,499,999 share 500,000.
        var result = ThetaIntersection.Of(Range(0, 1_000_000), Range(500_000, 1_000_000));

        Assert.True(result.IsEstimationMode);
        Assert.Equal(500_000, result.Estimate, 500_000 * SetOperationTolerance);
        Assert.InRange(500_000, result.GetLowerBound(3), result.GetUpperBound(3));
    }

    [Fact]
    public void IntersectionWithNoInputsHasNoResult()
    {
        var intersection = new ThetaIntersection();

        Assert.False(intersection.HasResult);
        // A virgin intersection is the universal set, not the empty set.
        Assert.Throws<InvalidOperationException>(() => intersection.GetResult());
    }

    [Fact]
    public void IntersectionReportsHavingAResultAfterOneInput()
    {
        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 100));

        Assert.True(intersection.HasResult);
    }

    [Fact]
    public void IntersectionRejectsNull()
    {
        var intersection = new ThetaIntersection();

        Assert.Throws<ArgumentNullException>(() => intersection.Intersect(null!));
    }

    [Fact]
    public void IntersectionResetRestoresUniversalSet()
    {
        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 1000));
        intersection.Reset();

        Assert.False(intersection.HasResult);

        intersection.Intersect(Range(0, 500));
        Assert.Equal(500, intersection.GetResult().RetainedEntries);
    }

    [Fact]
    public void IntersectionGetResultIsRepeatableAndNonDestructive()
    {
        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 1_000_000));
        intersection.Intersect(Range(500_000, 1_000_000));

        byte[] first = intersection.GetResult().ToByteArray();
        Assert.Equal(first, intersection.GetResult().ToByteArray());

        intersection.Intersect(Range(750_000, 1_000_000));
        Assert.Equal(250_000, intersection.GetResult().Estimate, 250_000 * 0.05);
    }

    [Fact]
    public void IntersectionRejectsMismatchedSeeds()
    {
        var other = UpdateThetaSketch.Builder().SetSeed(12345).Build();
        other.Update(1L);

        var intersection = new ThetaIntersection();
        intersection.Intersect(Range(0, 100));

        Assert.Throws<InvalidDataException>(() => intersection.Intersect(other.Compact()));
    }

    [Fact]
    public void IntersectionAcceptsUpdateSketchesAndSerializedImages()
    {
        var updatable = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 1000; i++)
        {
            updatable.Update(i);
        }

        var intersection = new ThetaIntersection();
        intersection.Intersect(updatable);
        intersection.IntersectCompactImage(Range(500, 1000).ToByteArray());

        Assert.Equal(SortedHashes(Range(500, 500)), SortedHashes(intersection.GetResult()));
    }

    // ---------- A-not-B ----------

    [Fact]
    public void DifferenceOfExactSketchesIsExact()
    {
        // 0..999 minus 500..1499 leaves exactly 0..499.
        var result = ThetaAnotB.Of(Range(0, 1000), Range(500, 1000));

        Assert.False(result.IsEstimationMode);
        Assert.Equal(500, result.RetainedEntries);
        Assert.Equal(500.0, result.Estimate);
        Assert.Equal(SortedHashes(Range(0, 500)), SortedHashes(result));
    }

    [Fact]
    public void DifferenceIsAsymmetric()
    {
        var a = Range(0, 1000);
        var b = Range(500, 1000);

        // A minus B leaves 0..499; B minus A leaves 1000..1499.
        Assert.Equal(SortedHashes(Range(0, 500)), SortedHashes(ThetaAnotB.Of(a, b)));
        Assert.Equal(SortedHashes(Range(1000, 500)), SortedHashes(ThetaAnotB.Of(b, a)));
    }

    [Fact]
    public void DifferenceWithDisjointSketchLeavesEverything()
    {
        var a = Range(0, 1000);

        Assert.Equal(a.ToByteArray(), ThetaAnotB.Of(a, Range(1000, 1000)).ToByteArray());
    }

    [Fact]
    public void DifferenceWithItselfIsEmpty()
    {
        var a = Range(0, 1000);
        var result = ThetaAnotB.Of(a, a);

        Assert.True(result.IsEmpty);
        Assert.Equal(0.0, result.Estimate);
        Assert.Equal(TckData.Load("theta_n0_java.sk"), result.ToByteArray());
    }

    [Fact]
    public void DifferenceWithEmptyLeavesEverything()
    {
        var a = Range(0, 1000);

        Assert.Equal(a.ToByteArray(), ThetaAnotB.Of(a, Range(0, 0)).ToByteArray());
    }

    [Fact]
    public void DifferenceFromEmptyIsEmpty()
    {
        var result = ThetaAnotB.Of(Range(0, 0), Range(0, 1000));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void RepeatedNotBSubtractsEachInTurn()
    {
        // 0..1999 minus 0..499 minus 1500..1999 leaves 500..1499.
        var operation = new ThetaAnotB();
        operation.SetA(Range(0, 2000));
        operation.NotB(Range(0, 500));
        operation.NotB(Range(1500, 500));

        Assert.Equal(SortedHashes(Range(500, 1000)), SortedHashes(operation.GetResult()));
    }

    [Fact]
    public void SetAReplacesPreviousState()
    {
        var operation = new ThetaAnotB();
        operation.SetA(Range(0, 2000));
        operation.NotB(Range(0, 1000));
        operation.SetA(Range(0, 500));

        Assert.Equal(SortedHashes(Range(0, 500)), SortedHashes(operation.GetResult()));
    }

    [Fact]
    public void DifferenceEstimatesLargeInputs()
    {
        var result = ThetaAnotB.Of(Range(0, 1_000_000), Range(500_000, 1_000_000));

        Assert.True(result.IsEstimationMode);
        Assert.Equal(500_000, result.Estimate, 500_000 * SetOperationTolerance);
        Assert.InRange(500_000, result.GetLowerBound(3), result.GetUpperBound(3));
    }

    [Fact]
    public void DifferenceAcceptsUpdateSketchesOnBothSides()
    {
        var a = UpdateThetaSketch.Builder().Build();
        var b = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 1000; i++) { a.Update(i); }
        for (long i = 500; i < 1500; i++) { b.Update(i); }

        Assert.Equal(SortedHashes(Range(0, 500)), SortedHashes(ThetaAnotB.Of(a, b)));
    }

    [Fact]
    public void DifferenceDoesNotDisturbItsOperands()
    {
        var a = Range(0, 1000);
        byte[] before = a.ToByteArray();

        var operation = new ThetaAnotB();
        operation.SetA(a);
        operation.NotB(Range(500, 1000));
        operation.GetResult();

        Assert.Equal(before, a.ToByteArray());
    }

    [Fact]
    public void DifferenceGetResultIsRepeatable()
    {
        var operation = new ThetaAnotB();
        operation.SetA(Range(0, 1000));
        operation.NotB(Range(500, 1000));

        byte[] first = operation.GetResult().ToByteArray();
        Assert.Equal(first, operation.GetResult().ToByteArray());
    }

    [Fact]
    public void DifferenceResetRestoresEmpty()
    {
        var operation = new ThetaAnotB();
        operation.SetA(Range(0, 1000));
        operation.Reset();

        Assert.True(operation.GetResult().IsEmpty);
    }

    [Fact]
    public void DifferenceRejectsMismatchedSeeds()
    {
        var other = UpdateThetaSketch.Builder().SetSeed(12345).Build();
        other.Update(1L);

        var operation = new ThetaAnotB();
        operation.SetA(Range(0, 1000));

        Assert.Throws<InvalidDataException>(() => operation.NotB(other.Compact()));
    }

    [Fact]
    public void DifferenceOfRejectsNull()
    {
        var a = Range(0, 100);

        Assert.Throws<ArgumentNullException>(() => ThetaAnotB.Of(null!, a));
        Assert.Throws<ArgumentNullException>(() => ThetaAnotB.Of(a, null!));
    }

    // ---------- The operations against each other ----------

    [Fact]
    public void InclusionExclusionHoldsInExactMode()
    {
        // |A| + |B| = |A union B| + |A intersect B|
        var a = Range(0, 1000);
        var b = Range(500, 1000);

        var union = new ThetaUnion();
        union.Union(a);
        union.Union(b);

        double unionCount = union.GetResult().Estimate;
        double intersectionCount = ThetaIntersection.Of(a, b).Estimate;

        Assert.Equal(a.Estimate + b.Estimate, unionCount + intersectionCount);
    }

    [Fact]
    public void DifferenceAndIntersectionPartitionTheirOperand()
    {
        // A = (A minus B) union (A intersect B), disjointly.
        var a = Range(0, 1000);
        var b = Range(500, 1000);

        var difference = ThetaAnotB.Of(a, b);
        var intersection = ThetaIntersection.Of(a, b);

        Assert.Equal(a.RetainedEntries, difference.RetainedEntries + intersection.RetainedEntries);

        long[] recombined = [.. SortedHashes(difference), .. SortedHashes(intersection)];
        Array.Sort(recombined);
        Assert.Equal(SortedHashes(a), recombined);
    }

    [Fact]
    public void SetOperationsComposeThroughSerialization()
    {
        // Round-trip each intermediate, as a pipeline across processes would.
        var a = CompactThetaSketch.Deserialize(Range(0, 100_000).ToByteArray());
        var b = CompactThetaSketch.Deserialize(Range(50_000, 100_000).ToByteArray());

        var intersection = CompactThetaSketch.Deserialize(ThetaIntersection.Of(a, b).ToByteArray());
        var difference = CompactThetaSketch.Deserialize(ThetaAnotB.Of(a, b).ToByteArray());

        Assert.Equal(50_000, intersection.Estimate, 50_000 * SetOperationTolerance);
        Assert.Equal(50_000, difference.Estimate, 50_000 * SetOperationTolerance);
        Assert.InRange(50_000, intersection.GetLowerBound(3), intersection.GetUpperBound(3));
        Assert.InRange(50_000, difference.GetLowerBound(3), difference.GetUpperBound(3));
    }
}
