// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// Base class for Theta sketches — distinct-count estimators that also support
/// set operations (union, intersection, difference) on the sketches themselves.
/// </summary>
/// <remarks>
/// <para>
/// A Theta sketch keeps a sample of the hash space: every retained hash is
/// below a threshold <c>theta</c>, and the distinct count is estimated as
/// <c>retained / theta</c>. Because the sample is determined by the hash values
/// alone and not by insertion order, two sketches over different data can be
/// combined exactly — which is what distinguishes Theta from a plain
/// distinct-count estimator.
/// </para>
/// <para>
/// Derive-ability is deliberately internal: the serialized layouts are fixed by
/// the DataSketches format, so a third-party subclass could not be serialized
/// meaningfully.
/// </para>
/// </remarks>
public abstract class ThetaSketch
{
    /// <summary>
    /// The update seed used by every DataSketches implementation unless told
    /// otherwise. Sketches built with different seeds cannot be merged.
    /// </summary>
    public const ulong DefaultUpdateSeed = 9001UL;

    /// <summary>Theta expressed on the wire: <see cref="long.MaxValue"/> means theta = 1.0.</summary>
    private const double LongMaxValueAsDouble = long.MaxValue;

    internal ThetaSketch()
    {
    }

    /// <summary>The sketch family, as recorded in the serialized preamble.</summary>
    public abstract SketchFamily Family { get; }

    /// <summary>True if this sketch has seen no values at all.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>True if the retained hashes are in ascending order.</summary>
    public abstract bool IsOrdered { get; }

    /// <summary>The number of hashes the sketch is currently holding.</summary>
    public abstract int RetainedEntries { get; }

    /// <summary>The 16-bit hash of the update seed this sketch was built with.</summary>
    public abstract ushort SeedHash { get; }

    /// <summary>
    /// The sketch's raw backing array. For a compact sketch this is the gap-free
    /// hash list; for an update sketch it is the hash table, so it may hold
    /// zeros (empty slots) and, transiently, values at or above theta. Consumers
    /// must filter.
    /// </summary>
    internal abstract long[] HashCache { get; }

    /// <summary>
    /// Theta in its stored form, a positive <see cref="long"/> where
    /// <see cref="long.MaxValue"/> represents 1.0.
    /// </summary>
    public abstract long ThetaLong { get; }

    /// <summary>Theta as a fraction in (0, 1].</summary>
    public double Theta => ThetaLong / LongMaxValueAsDouble;

    /// <summary>
    /// True if the sketch is sampling and its count is therefore an estimate.
    /// False when the sketch held everything it saw, in which case
    /// <see cref="Estimate"/> is exact.
    /// </summary>
    public bool IsEstimationMode => ThetaLong < long.MaxValue && !IsEmpty;

    /// <summary>
    /// The estimated number of distinct values. Exact when
    /// <see cref="IsEstimationMode"/> is false.
    /// </summary>
    /// <remarks>
    /// Virtual because the Alpha sketch decays theta continuously and reads its
    /// estimate off theta rather than off the retained count.
    /// </remarks>
    public virtual double Estimate => RetainedEntries * (LongMaxValueAsDouble / ThetaLong);

    /// <summary>
    /// A lower confidence bound on the distinct count.
    /// </summary>
    /// <param name="numStdDev">
    /// 1, 2, or 3 standard deviations — roughly 68%, 95%, or 99.7% confidence.
    /// Defaults to 2.
    /// </param>
    /// <remarks>
    /// Returns the exact count when the sketch is not estimating, since there is
    /// then nothing to be uncertain about. The interval is a binomial confidence
    /// interval on the sampling process, not a guarantee: at 2 standard
    /// deviations roughly one sketch in twenty will hold a true count outside
    /// <see cref="GetLowerBound"/>..<see cref="GetUpperBound"/>.
    /// </remarks>
    public virtual double GetLowerBound(int numStdDev = 2)
    {
        CheckNumStdDev(numStdDev);
        return IsEstimationMode
            ? BinomialBounds.LowerBound(RetainedEntries, Theta, numStdDev, IsEmpty)
            : RetainedEntries;
    }

    /// <summary>
    /// An upper confidence bound on the distinct count.
    /// </summary>
    /// <param name="numStdDev">
    /// 1, 2, or 3 standard deviations — roughly 68%, 95%, or 99.7% confidence.
    /// Defaults to 2.
    /// </param>
    /// <remarks>See <see cref="GetLowerBound"/>.</remarks>
    public virtual double GetUpperBound(int numStdDev = 2)
    {
        CheckNumStdDev(numStdDev);
        return IsEstimationMode
            ? BinomialBounds.UpperBound(RetainedEntries, Theta, numStdDev, IsEmpty)
            : RetainedEntries;
    }

    /// <summary>
    /// Validates the confidence level. The reference implementation only checks
    /// this on the estimating path; checking it always turns a silently
    /// meaningless argument into an error.
    /// </summary>
    private protected static void CheckNumStdDev(int numStdDev)
    {
        if (numStdDev is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numStdDev), numStdDev, "Number of standard deviations must be 1, 2, or 3.");
        }
    }

    /// <summary>
    /// Returns this sketch in immutable, ordered compact form — the form that
    /// serializes.
    /// </summary>
    public CompactThetaSketch Compact() => Compact(ordered: true);

    /// <summary>Returns this sketch in immutable compact form.</summary>
    /// <param name="ordered">
    /// Whether to sort the retained hashes. Ordered is what the reference
    /// implementations emit; unordered saves the sort when the result feeds
    /// straight into another set operation.
    /// </param>
    public abstract CompactThetaSketch Compact(bool ordered);

    /// <summary>Serializes the sketch to its DataSketches-compatible byte image.</summary>
    public abstract byte[] ToByteArray();
}
