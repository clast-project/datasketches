// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// The QuickSelect Theta update sketch — the default, and the one every
/// reference implementation builds unless told otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The sketch keeps theta at 1.0 and accepts everything until its hash table
/// fills. Only then does it act: it selects the (k+1)-th smallest retained hash
/// as the new theta and sweeps out everything at or above it, in one pass. That
/// batching is the whole idea — most updates are a single probe and nothing
/// else, and the expensive selection happens once per k/16 or so inserts.
/// </para>
/// <para>
/// The table grows to 2k slots before the first sweep, so a sketch can retain
/// meaningfully more than k entries between rebuilds. Call
/// <see cref="Rebuild"/> to force it back down to k.
/// </para>
/// </remarks>
internal sealed class QuickSelectThetaSketch : UpdateThetaSketch
{
    private readonly SketchFamily _family;

    private long[] _cache;
    private int _lgArrLongs;
    private int _hashTableThreshold;
    private int _curCount;
    private long _thetaLong;
    private bool _empty;

    public QuickSelectThetaSketch(
        int lgNominalEntries,
        ulong seed,
        float samplingProbability,
        ResizeFactor resizeFactor,
        bool unionGadget = false)
        : base(lgNominalEntries, seed, samplingProbability, resizeFactor)
    {
        _family = unionGadget ? SketchFamily.Union : SketchFamily.QuickSelect;
        _lgArrLongs = ThetaLimits.StartingSubMultiple(
            LgNominalEntries + 1, resizeFactor.Lg(), ThetaLimits.MinLgArrLongs);
        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, _lgArrLongs);
        _curCount = 0;
        _thetaLong = ThetaLimits.ThetaFromSamplingProbability(samplingProbability);
        _empty = true;
        _cache = new long[1 << _lgArrLongs];
    }

    public override SketchFamily Family => _family;

    public override bool IsEmpty => _empty;

    /// <summary>An update sketch stores hashes in probe order, never sorted.</summary>
    public override bool IsOrdered => false;

    public override int RetainedEntries => _curCount;

    /// <summary>
    /// An empty sketch reports theta = 1.0 even when a sampling probability
    /// below 1.0 has already lowered the internal value; otherwise readers would
    /// see the contradictory combination of "empty" and "estimating".
    /// </summary>
    public override long ThetaLong => _empty ? long.MaxValue : _thetaLong;

    internal override long[] HashCache => _cache;

    /// <summary>
    /// A union's gadget carries a fourth preamble long for the union's own
    /// theta, which is tracked separately from the gadget's.
    /// </summary>
    internal override int CurrentPreambleLongs => _family == SketchFamily.Union ? 4 : 3;

    internal override int LgArrLongs => _lgArrLongs;

    /// <summary>QuickSelect sweeps as part of the rebuild, so its table is never left dirty.</summary>
    internal override bool IsDirty => false;

    internal override ThetaUpdateResult HashUpdate(long hash)
    {
        ThetaHashTable.CheckHashCorruption(hash);
        _empty = false;

        if (ThetaHashTable.ContinueCondition(_thetaLong, hash))
        {
            return ThetaUpdateResult.RejectedOverTheta;
        }

        if (ThetaHashTable.SearchOrInsert(_cache, _lgArrLongs, hash) >= 0)
        {
            return ThetaUpdateResult.RejectedDuplicate;
        }

        _curCount++;

        if (_curCount > _hashTableThreshold)
        {
            if (_lgArrLongs <= LgNominalEntries)
            {
                Resize();
            }
            else
            {
                QuickSelectAndRebuild();
            }
        }

        return ThetaUpdateResult.Inserted;
    }

    internal override void LoadState(int lgArrLongs, int retained, long thetaLong, bool empty, long[] cache)
    {
        _lgArrLongs = lgArrLongs;
        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, lgArrLongs);
        _curCount = retained;
        _thetaLong = thetaLong;
        _empty = empty;
        _cache = cache;
    }

    public override UpdateThetaSketch Rebuild()
    {
        if (_curCount > (1 << LgNominalEntries))
        {
            QuickSelectAndRebuild();
        }
        return this;
    }

    public override void Reset()
    {
        int lgArrLongs = ThetaLimits.StartingSubMultiple(
            LgNominalEntries + 1, ResizeFactor.Lg(), ThetaLimits.MinLgArrLongs);

        if (lgArrLongs == _lgArrLongs)
        {
            Array.Clear(_cache, 0, _cache.Length);
        }
        else
        {
            _cache = new long[1 << lgArrLongs];
            _lgArrLongs = lgArrLongs;
        }

        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, _lgArrLongs);
        _empty = true;
        _curCount = 0;
        _thetaLong = ThetaLimits.ThetaFromSamplingProbability(SamplingProbability);
    }

    /// <summary>
    /// Grows the table toward its 2k ceiling and re-probes everything into it.
    /// Theta and the retained count are unchanged.
    /// </summary>
    private void Resize()
    {
        int lgMaxArrLongs = LgNominalEntries + 1;
        int lgDelta = lgMaxArrLongs - _lgArrLongs;
        // A resize factor of X1 still has to make progress, hence the floor of 1;
        // and never overshoot the 2k ceiling, hence the cap at the remaining delta.
        int lgResize = Math.Max(Math.Min(ResizeFactor.Lg(), lgDelta), 1);
        _lgArrLongs += lgResize;

        long[] target = new long[1 << _lgArrLongs];
        _curCount = ThetaHashTable.ArrayInsert(_cache, target, _lgArrLongs, _thetaLong);
        _cache = target;
        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, _lgArrLongs);
    }

    /// <summary>
    /// Drops theta to the (k+1)-th smallest retained hash and sweeps out
    /// everything at or above it. The table stays the same size; the retained
    /// count falls to roughly k.
    /// </summary>
    private void QuickSelectAndRebuild()
    {
        int arrLongs = 1 << _lgArrLongs;
        int pivot = (1 << LgNominalEntries) + 1;

        // Reorders _cache as a side effect, which is fine — the table is about to
        // be rebuilt from scratch anyway.
        _thetaLong = QuickSelect.SelectExcludingZeros(_cache, _curCount, pivot);

        long[] target = new long[arrLongs];
        _curCount = ThetaHashTable.ArrayInsert(_cache, target, _lgArrLongs, _thetaLong);
        _cache = target;
        // The threshold depends only on the table size, which did not change.
    }
}
