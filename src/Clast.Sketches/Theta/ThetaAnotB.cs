// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// Set difference for Theta sketches: estimates how many distinct values are in
/// <c>A</c> but not in <c>B</c>.
/// </summary>
/// <remarks>
/// <para>
/// Like an intersection, this works because the operands sample the same region
/// of hash space, so a retained hash of <c>A</c> that is absent from <c>B</c>'s
/// retained set really was absent from <c>B</c> — provided the hash is below
/// both thetas, which is why the operation lowers theta to the minimum of the
/// two before comparing.
/// </para>
/// <para>
/// The same accuracy caveat applies: when <c>A</c> and <c>B</c> nearly coincide,
/// the difference is a small number recovered from two large ones and its
/// relative error is correspondingly wide. Check the result's bounds.
/// </para>
/// <para>
/// Unlike a union or intersection, this operation is asymmetric and its
/// operands play different roles, so it is driven with <see cref="SetA"/> and
/// <see cref="NotB"/> rather than a single accumulating method. For the common
/// two-operand case use <see cref="Of"/>.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class ThetaAnotB
{
    private readonly ulong _seed;
    private readonly ushort _seedHash;

    private long[] _hashArr = [];
    private int _curCount;
    private long _thetaLong;
    private bool _empty;

    /// <summary>Creates a set-difference operation.</summary>
    /// <param name="seed">The update seed. Both operands must share it.</param>
    public ThetaAnotB(ulong seed = ThetaSketch.DefaultUpdateSeed)
    {
        _seed = seed;
        _seedHash = SeedHashes.Compute(seed);
        Reset();
    }

    /// <summary>The 16-bit hash of the update seed both operands must share.</summary>
    public ushort SeedHash => _seedHash;

    /// <summary>
    /// Sets the sketch to subtract from, discarding any previous state.
    /// </summary>
    /// <exception cref="InvalidDataException">The sketch was built with a different update seed.</exception>
    public void SetA(ThetaSketch? a)
    {
        if (a is null || a.IsEmpty)
        {
            // Nothing to subtract from; the difference is empty whatever B is.
            Reset();
            return;
        }

        SeedHashes.Check(a.SeedHash, _seedHash);

        _hashArr = CompactHashesOf(a);
        _empty = false;
        _thetaLong = a.ThetaLong;
        _curCount = _hashArr.Length;
    }

    /// <summary>
    /// Subtracts a sketch. May be called repeatedly to subtract several sketches
    /// in turn.
    /// </summary>
    /// <exception cref="InvalidDataException">The sketch was built with a different update seed.</exception>
    public void NotB(ThetaSketch? b)
    {
        if (_empty || b is null || b.IsEmpty)
        {
            // Subtracting nothing changes nothing, and nothing minus anything is
            // still nothing.
            return;
        }

        SeedHashes.Check(b.SeedHash, _seedHash);

        _thetaLong = Math.Min(_thetaLong, b.ThetaLong);
        _hashArr = Subtract(_thetaLong, _curCount, _hashArr, b);
        _curCount = _hashArr.Length;
        // Retaining nothing at theta = 1.0 means the difference really is empty,
        // not merely estimated as zero.
        _empty = _curCount == 0 && _thetaLong == long.MaxValue;
    }

    /// <summary>Returns the difference in ordered compact form.</summary>
    public CompactThetaSketch GetResult() => GetResult(ordered: true);

    /// <summary>
    /// Returns the difference in compact form. Non-destructive: call
    /// <see cref="Reset"/> or <see cref="SetA"/> to start over.
    /// </summary>
    /// <param name="ordered">Whether to sort the retained hashes.</param>
    public CompactThetaSketch GetResult(bool ordered)
    {
        long[] hashes = (long[])_hashArr.Clone();
        if (ordered && hashes.Length > 1)
        {
            // The working array is in A's order, which carries no meaning here.
            ThetaSort.Sort(hashes, hashes.Length, _thetaLong);
        }
        return new CompactThetaSketch(hashes, _thetaLong, _empty, ordered, _seedHash);
    }

    /// <summary>Returns the operation to its initial empty state.</summary>
    public void Reset()
    {
        _thetaLong = long.MaxValue;
        _empty = true;
        _hashArr = [];
        _curCount = 0;
    }

    /// <summary>Computes <c>a</c> minus <c>b</c> in one call.</summary>
    /// <exception cref="ArgumentNullException">Either sketch is null.</exception>
    /// <exception cref="InvalidDataException">The sketches were built with different update seeds.</exception>
    public static CompactThetaSketch Of(
        ThetaSketch a, ThetaSketch b, ulong seed = ThetaSketch.DefaultUpdateSeed, bool ordered = true)
    {
        if (a is null) { throw new ArgumentNullException(nameof(a)); }
        if (b is null) { throw new ArgumentNullException(nameof(b)); }

        var operation = new ThetaAnotB(seed);
        operation.SetA(a);
        operation.NotB(b);
        return operation.GetResult(ordered);
    }

    /// <summary>
    /// Keeps the hashes of <paramref name="hashArrA"/> that are below
    /// <paramref name="minThetaLong"/> and absent from <paramref name="b"/>.
    /// </summary>
    private static long[] Subtract(long minThetaLong, int countA, long[] hashArrA, ThetaSketch b)
    {
        // A compact sketch stores a sorted list rather than a probe table, so it
        // has to be rebuilt into one before it can be searched. An update
        // sketch already is one.
        long[] tableB;
        int lgTableB;
        if (b is UpdateThetaSketch updatableB)
        {
            tableB = updatableB.HashCache;
            lgTableB = updatableB.LgArrLongs;
        }
        else
        {
            lgTableB = ThetaHashTable.MinLgHashTableSize(b.RetainedEntries, ThetaLimits.RebuildThreshold);
            tableB = BuildHashTable(b, minThetaLong, lgTableB);
        }

        long[] kept = new long[countA];
        int keptCount = 0;
        for (int i = 0; i < countA; i++)
        {
            long hash = hashArrA[i];
            // Only hashes below the shared theta can be reasoned about: above it,
            // B's silence is not evidence of absence.
            if (hash != 0L && hash < minThetaLong
                && ThetaHashTable.Search(tableB, lgTableB, hash) == -1)
            {
                kept[keptCount++] = hash;
            }
        }

        if (keptCount == kept.Length)
        {
            return kept;
        }

        long[] trimmed = new long[keptCount];
        Array.Copy(kept, trimmed, keptCount);
        return trimmed;
    }

    /// <summary>Rebuilds a compact sketch's sorted hash list into a probe table.</summary>
    private static long[] BuildHashTable(ThetaSketch sketch, long thetaLong, int lgArrLongs)
    {
        ThetaHashTable.CheckThetaCorruption(thetaLong);

        long[] table = new long[1 << lgArrLongs];
        long sketchTheta = sketch.ThetaLong;
        foreach (long hash in sketch.HashCache)
        {
            if (hash <= 0L || hash >= sketchTheta || ThetaHashTable.ContinueCondition(thetaLong, hash))
            {
                continue;
            }
            ThetaHashTable.SearchOrInsert(table, lgArrLongs, hash);
        }
        return table;
    }

    /// <summary>
    /// A private copy of a sketch's retained hashes. Always a copy: the array
    /// becomes this operation's working state and gets replaced and sorted, which
    /// must not disturb the caller's sketch.
    /// </summary>
    private static long[] CompactHashesOf(ThetaSketch sketch) =>
        sketch is CompactThetaSketch compact
            ? (long[])compact.HashCache.Clone()
            // Ordering is irrelevant here and the compaction already allocates.
            : sketch.Compact(ordered: false).HashCache;
}
