// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// Intersects Theta sketches, estimating how many distinct values they have in
/// common.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a union, an intersection cannot be computed from the estimates alone
/// — knowing two sets have a million elements each says nothing about their
/// overlap. It works here because the sketches sample the <em>same</em> region
/// of hash space: a value below theta is retained by every sketch that saw it,
/// so matching retained hashes really do represent shared values.
/// </para>
/// <para>
/// That also explains the accuracy characteristics. The relative error is
/// governed by the size of the intersection compared to the operands, so
/// intersecting two large sets that share very little gives a result with wide
/// relative error even though each input was accurate. Check
/// <see cref="ThetaSketch.GetLowerBound"/> and
/// <see cref="ThetaSketch.GetUpperBound"/> on the result before trusting it.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class ThetaIntersection
{
    /// <summary>
    /// Retained count sentinel for a fresh intersection, which represents the
    /// universal set rather than the empty one — intersecting with it yields
    /// whatever it is intersected with.
    /// </summary>
    private const int UniversalSet = -1;

    private readonly ulong _seed;
    private readonly ushort _seedHash;

    private long[]? _hashTable;
    private int _lgArrLongs;
    private int _curCount;
    private long _thetaLong;
    private bool _empty;

    /// <summary>Creates an intersection.</summary>
    /// <param name="seed">The update seed. Every sketch intersected must share it.</param>
    /// <remarks>
    /// There is no nominal entry count: an intersection only ever shrinks, so it
    /// sizes itself to the result rather than to a configured <c>k</c>.
    /// </remarks>
    public ThetaIntersection(ulong seed = ThetaSketch.DefaultUpdateSeed)
    {
        _seed = seed;
        _seedHash = SeedHashes.Compute(seed);
        HardReset();
    }

    /// <summary>The 16-bit hash of the update seed every input must share.</summary>
    public ushort SeedHash => _seedHash;

    /// <summary>
    /// True once at least one sketch has been intersected in. Until then the
    /// intersection stands for the universal set, which has no finite result.
    /// </summary>
    public bool HasResult => _curCount >= 0;

    /// <summary>
    /// Intersects a sketch in.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// The sketch is null. Unlike a union, null is not treated as the empty set:
    /// intersecting with the empty set collapses the result to empty, which is
    /// too destructive to do by accident.
    /// </exception>
    /// <exception cref="InvalidDataException">The sketch was built with a different update seed.</exception>
    public void Intersect(ThetaSketch sketch)
    {
        if (sketch is null)
        {
            throw new ArgumentNullException(nameof(sketch));
        }

        if (_empty || sketch.IsEmpty)
        {
            // Anything intersected with the empty set is empty, and once empty
            // it stays empty regardless of what follows.
            ResetToEmpty();
            return;
        }

        SeedHashes.Check(sketch.SeedHash, _seedHash);

        _thetaLong = Math.Min(_thetaLong, sketch.ThetaLong);
        _empty = false;

        int incoming = sketch.RetainedEntries;

        if (_curCount == 0 || incoming == 0)
        {
            // One side retains nothing below the shared theta, so neither can the
            // result. Drop the table rather than keep an empty one around.
            _curCount = 0;
            _hashTable = null;
        }
        else if (_curCount == UniversalSet)
        {
            // First sketch in: the result is simply a copy of it.
            _curCount = incoming;
            _lgArrLongs = ThetaHashTable.MinLgHashTableSize(_curCount, ThetaLimits.RebuildThreshold);
            _hashTable = new long[1 << _lgArrLongs];
            CopyInto(sketch);
        }
        else
        {
            PerformIntersect(sketch);
        }
    }

    /// <summary>Intersects a serialized compact sketch in.</summary>
    /// <exception cref="InvalidDataException">The image is malformed or was built with a different seed.</exception>
    public void IntersectCompactImage(ReadOnlySpan<byte> compactImage) =>
        Intersect(CompactThetaSketch.Deserialize(compactImage, _seed));

    /// <summary>Returns the intersection in ordered compact form.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been intersected in yet.</exception>
    public CompactThetaSketch GetResult() => GetResult(ordered: true);

    /// <summary>
    /// Returns the intersection in compact form. Non-destructive: the
    /// intersection can keep accepting sketches afterwards.
    /// </summary>
    /// <param name="ordered">Whether to sort the retained hashes.</param>
    /// <exception cref="InvalidOperationException">Nothing has been intersected in yet.</exception>
    public CompactThetaSketch GetResult(bool ordered)
    {
        if (_curCount == UniversalSet)
        {
            throw new InvalidOperationException(
                "An intersection with no inputs represents the universal set, which has no finite result. " +
                "Intersect at least one sketch first.");
        }

        if (_curCount == 0 || _hashTable is null)
        {
            return new CompactThetaSketch([], _thetaLong, _empty, ordered: true, _seedHash);
        }

        // Only the live prefix of the table is meaningful — a shrunk table keeps
        // stale values past it that the masked probe indices never reach.
        long[] hashes = ThetaHashTable.CompactCache(
            _hashTable.AsSpan(0, 1 << _lgArrLongs), _curCount, _thetaLong, ordered);
        return new CompactThetaSketch(hashes, _thetaLong, _empty, ordered, _seedHash);
    }

    /// <summary>Returns the intersection to its initial universal-set state.</summary>
    public void Reset() => HardReset();

    /// <summary>Intersects two sketches in one call.</summary>
    /// <exception cref="InvalidDataException">The sketches were built with different update seeds.</exception>
    public static CompactThetaSketch Of(
        ThetaSketch a, ThetaSketch b, ulong seed = ThetaSketch.DefaultUpdateSeed, bool ordered = true)
    {
        var intersection = new ThetaIntersection(seed);
        intersection.Intersect(a);
        intersection.Intersect(b);
        return intersection.GetResult(ordered);
    }

    /// <summary>
    /// Matches the incoming sketch against the current table, keeping only
    /// hashes present in both.
    /// </summary>
    private void PerformIntersect(ThetaSketch sketch)
    {
        long[] table = _hashTable!;
        // The result cannot be larger than the smaller operand.
        long[] matches = new long[Math.Min(_curCount, sketch.RetainedEntries)];
        int matchCount = 0;

        bool ordered = sketch.IsOrdered;
        long sketchTheta = sketch.ThetaLong;
        long[] cache = sketch.HashCache;

        for (int i = 0; i < cache.Length; i++)
        {
            long hash = cache[i];
            if (hash <= 0L || hash >= sketchTheta)
            {
                continue;
            }

            if (hash < _thetaLong)
            {
                if (ThetaHashTable.Search(table.AsSpan(0, 1 << _lgArrLongs), _lgArrLongs, hash) != -1)
                {
                    matches[matchCount++] = hash;
                }
            }
            else if (ordered)
            {
                break;
            }
        }

        _curCount = matchCount;
        _lgArrLongs = ThetaHashTable.MinLgHashTableSize(matchCount, ThetaLimits.RebuildThreshold);

        // The table only ever shrinks, so the existing array is still big enough;
        // clearing the new live prefix is all the rebuild needs.
        Array.Clear(table, 0, 1 << _lgArrLongs);

        if (_curCount > 0)
        {
            for (int i = 0; i < matchCount; i++)
            {
                ThetaHashTable.InsertOnly(table, _lgArrLongs, matches[i]);
            }
        }
        else if (_thetaLong == long.MaxValue)
        {
            // Nothing in common and no sampling in play, so the result is exactly
            // the empty set rather than merely an estimate of zero.
            _empty = true;
        }
    }

    /// <summary>Copies a sketch's valid hashes into the freshly sized table.</summary>
    private void CopyInto(ThetaSketch sketch)
    {
        long sketchTheta = sketch.ThetaLong;
        foreach (long hash in sketch.HashCache)
        {
            if (hash <= 0L || hash >= sketchTheta || ThetaHashTable.ContinueCondition(_thetaLong, hash))
            {
                continue;
            }
            ThetaHashTable.InsertOnly(_hashTable!, _lgArrLongs, hash);
        }
    }

    private void HardReset()
    {
        ResetCommon();
        _curCount = UniversalSet;
        _empty = false;
    }

    private void ResetToEmpty()
    {
        ResetCommon();
        _curCount = 0;
        _empty = true;
    }

    private void ResetCommon()
    {
        _lgArrLongs = ThetaLimits.MinLgArrLongs;
        _thetaLong = long.MaxValue;
        _hashTable = null;
    }
}
