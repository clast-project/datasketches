// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// Merges HLL sketches, estimating the distinct count of everything they
/// collectively saw.
/// </summary>
/// <remarks>
/// <para>
/// HLL registers hold a maximum, so merging is register-wise maximum and the
/// result is exactly the sketch you would have built over the union of the
/// inputs — no accumulated error from repeated merging.
/// </para>
/// <para>
/// Three things make this less trivial than it sounds, and all are handled
/// here:
/// </para>
/// <list type="bullet">
/// <item><b>Mixed accuracies.</b> Sketches built with different <c>k</c> can be
/// merged: the larger is folded down to the smaller, since a coarser sketch
/// cannot be refined. The result's accuracy is that of the coarsest input.</item>
/// <item><b>Mixed register widths.</b> The union works internally in
/// <see cref="TgtHllType.Hll8"/>, whose byte-per-register layout merges without
/// unpacking, and converts on the way out.</item>
/// <item><b>The HIP estimator is lost.</b> It depends on having observed every
/// update in order, which a merge has not. Merged results fall back to the
/// composite estimator, and say so through their serialized flags.</item>
/// </list>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class HllUnion
{
    private readonly int _lgMaxK;
    private HllSketch _gadget;

    /// <summary>
    /// Creates a union.
    /// </summary>
    /// <param name="lgMaxK">
    /// Base-2 logarithm of the largest <c>k</c> the result may use, between 4
    /// and 21. Inputs coarser than this pull the result down with them; inputs
    /// finer than this are folded to it.
    /// </param>
    public HllUnion(int lgMaxK = HllUtil.DefaultLgK)
    {
        _lgMaxK = HllUtil.CheckLgK(lgMaxK);
        _gadget = new HllSketch(lgMaxK, TgtHllType.Hll8);
    }

    /// <summary>
    /// The effective <c>lgK</c> of the result, which may be smaller than the
    /// configured maximum once a coarser sketch has been merged in.
    /// </summary>
    public int LgConfigK => _gadget.LgConfigK;

    /// <summary>True if nothing non-empty has been merged in.</summary>
    public bool IsEmpty => _gadget.IsEmpty;

    /// <summary>The estimated distinct count of everything merged so far.</summary>
    public double Estimate
    {
        get
        {
            RebuildAggregatesIfNeeded();
            return _gadget.Estimate;
        }
    }

    /// <summary>A lower confidence bound on the union's distinct count.</summary>
    /// <param name="numStdDev">1, 2, or 3 standard deviations. Defaults to 2.</param>
    public double GetLowerBound(int numStdDev = 2)
    {
        RebuildAggregatesIfNeeded();
        return _gadget.GetLowerBound(numStdDev);
    }

    /// <summary>An upper confidence bound on the union's distinct count.</summary>
    /// <param name="numStdDev">1, 2, or 3 standard deviations. Defaults to 2.</param>
    public double GetUpperBound(int numStdDev = 2)
    {
        RebuildAggregatesIfNeeded();
        return _gadget.GetUpperBound(numStdDev);
    }

    /// <summary>
    /// Merges a sketch in. Null and empty sketches change nothing.
    /// </summary>
    public void Update(HllSketch? sketch) => _gadget = UnionImpl(sketch, _gadget, _lgMaxK);

    /// <summary>Merges a serialized sketch in.</summary>
    /// <exception cref="InvalidDataException">The image is malformed.</exception>
    public void Update(ReadOnlySpan<byte> image) => Update(HllSketch.Deserialize(image));

    /// <summary>Merges another union's accumulated state in.</summary>
    public void Update(HllUnion? union)
    {
        if (union is not null)
        {
            _gadget = UnionImpl(union._gadget, _gadget, _lgMaxK);
        }
    }

    /// <summary>
    /// Returns the merged result, defaulting to the most compact register width.
    /// </summary>
    public HllSketch GetResult(TgtHllType tgtHllType = TgtHllType.Hll4)
    {
        RebuildAggregatesIfNeeded();
        return _gadget.CopyAs(tgtHllType);
    }

    /// <summary>Returns the union to its initial empty state.</summary>
    public void Reset() => _gadget = new HllSketch(_lgMaxK, TgtHllType.Hll8);

    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="gadget"/>, returning
    /// whichever sketch should now hold the accumulated state.
    /// </summary>
    /// <remarks>
    /// The result is sometimes the source rather than the gadget: when the gadget
    /// is still in a coupon mode and the source has reached HLL mode, it is far
    /// cheaper to copy the source's register array and replay the gadget's
    /// handful of coupons into it than the other way round. The reference calls
    /// this a "reverse merge".
    /// </remarks>
    private static HllSketch UnionImpl(HllSketch? source, HllSketch gadget, int lgMaxK)
    {
        if (source is null || source.IsEmpty)
        {
            return gadget;
        }

        HllCurMode srcMode = source.CurMode;

        // A coupon list is a handful of values; just replay them.
        if (srcMode == HllCurMode.List)
        {
            source.MergeTo(gadget);
            return gadget;
        }

        int srcLgK = source.LgConfigK;
        int gadgetLgK = gadget.LgConfigK;
        bool gadgetEmpty = gadget.IsEmpty;

        if (srcMode == HllCurMode.Set)
        {
            if (gadgetEmpty && srcLgK == gadgetLgK)
            {
                // Nothing to merge with, so adopt the source outright.
                return source.CopyAs(TgtHllType.Hll8);
            }
            source.MergeTo(gadget);
            return gadget;
        }

        // The source has reached HLL mode. What happens next depends on what the
        // gadget is holding and on how the two accuracies compare.
        bool srcExceedsMax = srcLgK > lgMaxK;

        if (gadgetEmpty)
        {
            // Adopt the source, folding it down first if it is finer than allowed.
            return srcExceedsMax
                ? Downsample(source, lgMaxK)
                : source.CopyAs(TgtHllType.Hll8);
        }

        if (gadget.CurMode != HllCurMode.Hll)
        {
            // Reverse merge: take the source's register array and replay the
            // gadget's coupons into it.
            HllSketch target = srcExceedsMax
                ? Downsample(source, lgMaxK)
                : source.CopyAs(TgtHllType.Hll8);
            gadget.MergeTo(target);
            return target;
        }

        // Both are in HLL mode: a register-wise merge. If the source is coarser
        // than the gadget, the gadget has to come down to meet it — accuracy the
        // source never had cannot be recovered.
        HllSketch destination = srcLgK < gadgetLgK ? Downsample(gadget, srcLgK) : gadget;
        MergeHllToHllMode(source, destination);
        destination.SetOutOfOrder(true);
        return destination;
    }

    /// <summary>
    /// Merges one HLL-mode sketch's registers into another's, folding if the
    /// source is finer.
    /// </summary>
    /// <remarks>
    /// Folding by masking the register index is valid because the register
    /// address comes from the low bits of the hash, so a sketch with <c>2^a</c>
    /// registers partitions the same hash space as one with <c>2^b</c> registers
    /// for <c>b &lt; a</c>, just more finely. Taking the maximum over each group
    /// gives exactly the coarser sketch's register.
    /// </remarks>
    private static void MergeHllToHllMode(HllSketch source, HllSketch target)
    {
        var src = (HllArray)source.Impl;
        var tgt = (Hll8Array)target.Impl;

        // Deliberately skips the HIP and KxQ bookkeeping per register; the flag
        // below makes them get recomputed in one pass before anyone reads them.
        if (src is Hll8Array src8)
        {
            // Both sides are a byte per register, so the merge is an element-wise
            // maximum over two arrays and needs no per-register decoding at all.
            HllRegisters.MaxIntoFolded(src8.ByteArray, tgt.ByteArray);
        }
        else
        {
            // A packed source has to be decoded a register at a time, but the
            // target is still written directly.
            byte[] tgtArr = tgt.ByteArray;
            int srcK = 1 << src.LgConfigK;
            int tgtKmask = tgtArr.Length - 1;

            for (int i = 0; i < srcK; i++)
            {
                int value = src.GetSlotValue(i);
                int j = i & tgtKmask;
                if (value > tgtArr[j])
                {
                    tgtArr[j] = (byte)value;
                }
            }
        }

        tgt.RebuildCurMinNumKxQ = true;
    }

    /// <summary>
    /// Copies an HLL-mode sketch to a coarser <c>lgK</c>, always landing on a
    /// heap HLL_8.
    /// </summary>
    private static HllSketch Downsample(HllSketch candidate, int tgtLgK)
    {
        var source = (HllArray)candidate.Impl;
        var target = new Hll8Array(tgtLgK);

        foreach (int pair in source.ValidPairs())
        {
            // The coupon path masks the register index down and rebuilds KxQ.
            target.CouponUpdate(pair);
        }

        // Both are needed for the downsampled sketch to behave like the original:
        // HIP records history the registers no longer carry, and the ordering flag
        // says whether HIP still applies.
        target.HipAccum = source.HipAccum;
        target.SetOutOfOrder(candidate.IsOutOfOrder);
        target.RebuildCurMinNumKxQ = false;
        return HllSketch.Wrap(target);
    }

    /// <summary>
    /// Recomputes the aggregates that the fast register-wise merge left stale.
    /// </summary>
    /// <remarks>
    /// One pass over the registers, done lazily so a run of merges pays for it
    /// once rather than per merge. HIP is untouched — it is already invalid for a
    /// merged sketch, which is why the out-of-order flag is set.
    /// </remarks>
    private void RebuildAggregatesIfNeeded()
    {
        if (_gadget.Impl is not HllArray array
            || !array.RebuildCurMinNumKxQ
            || array.TgtHllType != TgtHllType.Hll8)
        {
            return;
        }

        // Reads the register bytes directly rather than through the virtual
        // accessor, but keeps the reference's per-register accumulation order.
        // Summing by value instead would be far fewer operations, and wrong to
        // do: KxQ is serialized, and floating-point addition is not associative,
        // so reassociating would drift from the reference in the low bits.
        byte[] registers = ((Hll8Array)array).ByteArray;

        int curMin = 64;
        int numAtCurMin = 0;
        double kxq0 = 1 << array.LgConfigK;
        double kxq1 = 0;

        foreach (byte register in registers)
        {
            int v = register & HllUtil.ValMask6;
            if (v > 0)
            {
                // Each register contributes 2^-v; the initial k already counts
                // every register as if empty, so subtract the 1 it assumed.
                if (v < 32) { kxq0 += HllUtil.InvPow2(v) - 1.0; }
                else { kxq1 += HllUtil.InvPow2(v) - 1.0; }
            }

            if (v > curMin) { continue; }
            if (v < curMin)
            {
                curMin = v;
                numAtCurMin = 1;
            }
            else
            {
                numAtCurMin++;
            }
        }

        array.KxQ0 = kxq0;
        array.KxQ1 = kxq1;
        array.CurMin = curMin;
        array.NumAtCurMin = numAtCurMin;
        array.RebuildCurMinNumKxQ = false;
    }
}
