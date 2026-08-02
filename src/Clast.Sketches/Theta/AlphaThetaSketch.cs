// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// The Alpha Theta update sketch. This is the variant Apache Iceberg Puffin
/// specifies for its <c>apache-datasketches-theta-v1</c> blobs.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="QuickSelectThetaSketch"/> holds theta still and drops it in
/// a jump when the table fills, Alpha nudges theta down by a factor of
/// <c>k/(k+1)</c> on every insert past the k-th. The continuous decay gives a
/// noticeably better estimate for a single sketch — roughly 30% lower error at
/// the same k.
/// </para>
/// <para>
/// The cost is that entries already in the table can fall above the new theta
/// at any moment. Rather than sweep on every update, the sketch marks itself
/// dirty and tolerates those stale entries, cleaning them out opportunistically
/// during inserts and fully during a rebuild. So while dirty, the raw slot count
/// overstates the sketch and <see cref="RetainedEntries"/> has to count.
/// </para>
/// <para>
/// Two consequences: Alpha requires at least 512 nominal entries, and it is only
/// more accurate than QuickSelect as a standalone sketch — the advantage is lost
/// once the sketch is fed through a union, which is why QuickSelect remains the
/// default.
/// </para>
/// </remarks>
internal sealed class AlphaThetaSketch : UpdateThetaSketch
{
    /// <summary>Alpha needs a large enough k for its decay to behave; 512 is the reference floor.</summary>
    public const int MinLgNominalEntriesForAlpha = 9;

    private readonly double _alpha;
    private readonly long _split1;

    private long[] _cache;
    private int _lgArrLongs;
    private int _hashTableThreshold;
    private int _curCount;
    private long _thetaLong;
    private bool _empty;
    private bool _dirty;

    public AlphaThetaSketch(
        int lgNominalEntries, ulong seed, float samplingProbability, ResizeFactor resizeFactor)
        : base(lgNominalEntries, seed, samplingProbability, resizeFactor)
    {
        if (lgNominalEntries < MinLgNominalEntriesForAlpha)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lgNominalEntries),
                lgNominalEntries,
                $"The Alpha sketch requires at least {1 << MinLgNominalEntriesForAlpha} nominal entries.");
        }

        double nominal = 1L << LgNominalEntries;
        _alpha = nominal / (nominal + 1.0);
        // Above split1 the sketch has not yet seen k+1 inserts, so theta has never
        // been decayed and the plain retained/theta estimate applies.
        _split1 = (long)(samplingProbability * (_alpha + 1.0) / 2.0 * (double)long.MaxValue);

        _lgArrLongs = ThetaLimits.StartingSubMultiple(
            LgNominalEntries + 1, resizeFactor.Lg(), ThetaLimits.MinLgArrLongs);
        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, _lgArrLongs);
        _curCount = 0;
        _thetaLong = ThetaLimits.ThetaFromSamplingProbability(samplingProbability);
        _empty = true;
        _cache = new long[1 << _lgArrLongs];
    }

    public override SketchFamily Family => SketchFamily.Alpha;

    public override bool IsEmpty => _empty;

    public override bool IsOrdered => false;

    /// <summary>
    /// The number of entries actually below theta. While the sketch is dirty
    /// this has to be counted rather than read, because decaying theta can
    /// invalidate entries without touching them.
    /// </summary>
    public override int RetainedEntries =>
        _curCount > 0 && _dirty ? ThetaHashTable.CountValid(_cache, ThetaLong) : _curCount;

    public override long ThetaLong => _thetaLong;

    /// <summary>
    /// Alpha's estimator. Before the sketch reaches k+1 inserts theta has not
    /// decayed and the ordinary retained/theta formula holds; after that, the
    /// decay itself encodes the count, so the estimate reads off theta alone.
    /// </summary>
    public override double Estimate =>
        _thetaLong > _split1
            ? _curCount * ((double)long.MaxValue / _thetaLong)
            : (1 << LgNominalEntries) * ((double)long.MaxValue / _thetaLong);

    /// <summary>
    /// Alpha's bounds come from an HIP (Historic Inverse Probability) variance
    /// rather than the binomial interval the other sketches use. Its retained
    /// set is not a plain uniform sample — theta moved continuously while the
    /// sketch was being built — so the binomial model does not apply.
    /// </summary>
    public override double GetLowerBound(int numStdDev = 2)
    {
        CheckNumStdDev(numStdDev);
        if (!IsEstimationMode)
        {
            return _curCount;
        }

        int validCount = RetainedEntries;
        if (validCount == 0)
        {
            return 0.0;
        }

        double stdDev = Math.Sqrt(Variance(validCount));
        return Math.Max(Estimate - (numStdDev * stdDev), 0.0);
    }

    /// <inheritdoc cref="GetLowerBound"/>
    public override double GetUpperBound(int numStdDev = 2)
    {
        CheckNumStdDev(numStdDev);
        if (!IsEstimationMode)
        {
            return _curCount;
        }

        return Estimate + (numStdDev * Math.Sqrt(Variance(RetainedEntries)));
    }

    /// <summary>
    /// Error variance of the HIP estimator, which depends on how far the sketch
    /// has progressed: <c>r</c> distinguishes not-yet-decaying, just-decayed,
    /// and fully-decaying states, because the estimator's form differs in each.
    /// </summary>
    private double Variance(int count)
    {
        double k = 1 << LgNominalEntries;
        double p = SamplingProbability;
        double theta = Theta;

        double kPlus1 = k + 1.0;
        double y = 1.0 / p;
        double ySqMinusY = (y * y) - y;

        double result;
        int r = DecayState(theta, _alpha, p);
        if (r == 0)
        {
            result = count * ySqMinusY;
        }
        else if (r == 1)
        {
            result = kPlus1 * ySqMinusY;
        }
        else
        {
            double b = 1.0 / _alpha;
            double bSq = b * b;
            double x = p / theta;
            double xSq = x * x;
            double term1 = kPlus1 * ySqMinusY;
            double term2 = y / (1.0 - bSq);
            double term3 = (y * bSq) - (y * xSq) - b - bSq + x + (x * b);
            result = term1 + (term2 * term3);
        }

        return result + ((1 - theta) / (theta * theta));
    }

    /// <summary>
    /// How many times theta has decayed, computed from theta itself rather than
    /// tracked: 0 for none, 1 for exactly one, 2 for two or more.
    /// </summary>
    private static int DecayState(double theta, double alpha, double p)
    {
        double split1 = p * (alpha + 1.0) / 2.0;
        if (theta > split1) { return 0; }
        if (theta > alpha * split1) { return 1; }
        return 2;
    }

    internal override long[] HashCache => _cache;

    internal override int LgArrLongs => _lgArrLongs;

    internal override bool IsDirty => _dirty;

    internal override ThetaUpdateResult HashUpdate(long hash)
    {
        ThetaHashTable.CheckHashCorruption(hash);
        _empty = false;

        if (ThetaHashTable.ContinueCondition(_thetaLong, hash))
        {
            return ThetaUpdateResult.RejectedOverTheta;
        }

        if (_dirty)
        {
            return EnhancedHashInsert(hash);
        }

        if (ThetaHashTable.SearchOrInsert(_cache, _lgArrLongs, hash) >= 0)
        {
            return ThetaUpdateResult.RejectedDuplicate;
        }

        _curCount++;

        if (_thetaLong > _split1)
        {
            // Still counting toward the first k inserts; theta has not moved yet.
            if (_curCount > (1 << LgNominalEntries))
            {
                // The k+1-th insert. Switch into sketch mode: this is the one and
                // only transition, and from here every insert decays theta.
                _thetaLong = (long)(_thetaLong * _alpha);
                _dirty = true;
            }
            else if (_curCount > _hashTableThreshold)
            {
                ResizeClean();
            }
        }
        else
        {
            _thetaLong = (long)(_thetaLong * _alpha);
            _dirty = true;
            if (_curCount > _hashTableThreshold)
            {
                RebuildDirty();
            }
        }

        return ThetaUpdateResult.Inserted;
    }

    /// <summary>
    /// Insert that reclaims stale slots. If the probe path passes an entry that
    /// has fallen at or above theta, that slot is remembered and reused — but
    /// only after the search continues far enough to prove the value is not
    /// already present further along the path. Overwriting early would risk
    /// storing a duplicate.
    /// </summary>
    private ThetaUpdateResult EnhancedHashInsert(long hash)
    {
        int arrayMask = (1 << _lgArrLongs) - 1;
        int stride = ThetaHashTable.Stride(hash, _lgArrLongs);
        int curProbe = (int)(hash & arrayMask);
        long curTableHash = _cache[curProbe];
        int loopIndex = curProbe;

        while (curTableHash != hash && curTableHash != 0)
        {
            if (curTableHash >= _thetaLong)
            {
                int rememberPos = curProbe;

                curProbe = (curProbe + stride) & arrayMask;
                curTableHash = _cache[curProbe];
                while (curTableHash != hash && curTableHash != 0)
                {
                    curProbe = (curProbe + stride) & arrayMask;
                    curTableHash = _cache[curProbe];
                }

                if (curTableHash == hash)
                {
                    return ThetaUpdateResult.RejectedDuplicate;
                }

                // No duplicate on the path, so the stale slot is safe to reuse.
                // The count does not change: one stale entry out, one real in.
                _cache[rememberPos] = hash;
                _thetaLong = (long)(_thetaLong * _alpha);
                _dirty = true;
                return ThetaUpdateResult.Inserted;
            }

            curProbe = (curProbe + stride) & arrayMask;
            curTableHash = _cache[curProbe];

            if (curProbe == loopIndex)
            {
                throw new InvalidOperationException("Theta hash table is full and the value was not found.");
            }
        }

        if (curTableHash == hash)
        {
            return ThetaUpdateResult.RejectedDuplicate;
        }

        _cache[curProbe] = hash;
        _thetaLong = (long)(_thetaLong * _alpha);
        _dirty = true;
        if (++_curCount > _hashTableThreshold)
        {
            RebuildDirty();
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
        // Images are always written from a swept table — ToByteArray rebuilds
        // first — so a freshly loaded sketch is clean by construction.
        _dirty = false;
    }

    public override UpdateThetaSketch Rebuild()
    {
        if (_dirty)
        {
            RebuildDirty();
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
        _dirty = false;
    }

    /// <summary>
    /// Sweeps stale entries out. If that frees nothing — rare, but possible when
    /// theta has barely moved — the table has to grow instead, or the sketch
    /// would spin here on every subsequent insert.
    /// </summary>
    private void RebuildDirty()
    {
        int countBefore = _curCount;
        ForceRebuildDirtyCache();
        if (countBefore == _curCount)
        {
            ForceResizeCleanCache(1);
        }
    }

    /// <summary>Grows the table because it filled before theta started decaying.</summary>
    private void ResizeClean()
    {
        int lgTarget = LgNominalEntries + 1;
        if (lgTarget > _lgArrLongs)
        {
            int lgDelta = lgTarget - _lgArrLongs;
            int lgResize = Math.Max(Math.Min(ResizeFactor.Lg(), lgDelta), 1);
            ForceResizeCleanCache(lgResize);
        }
        else
        {
            // Already at full size with nothing stale to reclaim; grow anyway.
            ForceResizeCleanCache(1);
        }
    }

    /// <summary>Grows the table and re-probes. Theta and the count are unchanged.</summary>
    private void ForceResizeCleanCache(int lgResizeFactor)
    {
        _lgArrLongs += lgResizeFactor;
        long[] target = new long[1 << _lgArrLongs];
        _curCount = ThetaHashTable.ArrayInsert(_cache, target, _lgArrLongs, _thetaLong);
        _cache = target;
        _hashTableThreshold = ThetaLimits.HashTableThreshold(LgNominalEntries, _lgArrLongs);
    }

    /// <summary>Rebuilds at the same size, dropping everything at or above theta.</summary>
    private void ForceRebuildDirtyCache()
    {
        long[] target = new long[1 << _lgArrLongs];
        _curCount = ThetaHashTable.ArrayInsert(_cache, target, _lgArrLongs, _thetaLong);
        _cache = target;
        _dirty = false;
    }
}
