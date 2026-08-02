// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// The HLL register array, shared by all three register widths.
/// </summary>
/// <remarks>
/// <para>
/// Alongside the registers the sketch maintains running aggregates so the
/// estimate never requires a full scan:
/// </para>
/// <list type="bullet">
/// <item><c>KxQ0</c> and <c>KxQ1</c> hold <c>k</c> times the probability that
/// an incoming value would change the sketch. Two registers rather than one
/// because the sum spans a huge dynamic range, and splitting it at register
/// value 32 buys the mantissa precision that very large counts need.</item>
/// <item><c>HipAccum</c> is the HIP estimate, accumulated one increment per
/// state change. It is more accurate than the composite estimator but only
/// valid if the sketch saw every update itself.</item>
/// <item><c>CurMin</c> and <c>NumAtCurMin</c> track the smallest register value
/// and how many registers hold it. Only HLL_4 varies <c>CurMin</c>; for the
/// wider types it stays zero and the count doubles as a count of empty
/// registers.</item>
/// </list>
/// </remarks>
internal abstract class HllArray : HllSketchImpl
{
    protected byte[] HllByteArr;

    private bool _outOfOrder;

    protected HllArray(int lgConfigK, TgtHllType tgtHllType)
        : base(lgConfigK, tgtHllType, HllCurMode.Hll)
    {
        CurMin = 0;
        NumAtCurMin = 1 << lgConfigK;
        HipAccum = 0;
        KxQ0 = 1 << lgConfigK;
        KxQ1 = 0;
        HllByteArr = [];
    }

    protected HllArray(HllArray other)
        : base(other.LgConfigK, other.TgtHllType, HllCurMode.Hll)
    {
        _outOfOrder = other._outOfOrder;
        RebuildCurMinNumKxQ = other.RebuildCurMinNumKxQ;
        CurMin = other.CurMin;
        NumAtCurMin = other.NumAtCurMin;
        HipAccum = other.HipAccum;
        KxQ0 = other.KxQ0;
        KxQ1 = other.KxQ1;
        HllByteArr = (byte[])other.HllByteArr.Clone();
        AuxHashMap = other.AuxHashMap?.Copy();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Backed by a field rather than an auto-property because the base declares
    /// it read-only; only this class may change it.
    /// </remarks>
    public override bool IsOutOfOrder => _outOfOrder;

    public void SetOutOfOrder(bool value) => _outOfOrder = value;

    public bool RebuildCurMinNumKxQ { get; set; }

    public int CurMin { get; set; }

    public int NumAtCurMin { get; set; }

    public double HipAccum { get; set; }

    public double KxQ0 { get; set; }

    public double KxQ1 { get; set; }

    public AuxHashMap? AuxHashMap { get; set; }

    public byte[] ByteArray => HllByteArr;

    /// <summary>
    /// An HLL-mode sketch is empty only if no register was ever set, which for
    /// the wider types means every register is still at the minimum.
    /// </summary>
    public override bool IsEmpty => CurMin == 0 && NumAtCurMin == (1 << LgConfigK);

    public override int PreInts => HllPreamble.HllPreInts;

    public override int DataStart => HllPreamble.HllByteArrStart;

    /// <summary>Byte offset where the auxiliary table would begin, for HLL_4.</summary>
    public int AuxStart => HllPreamble.HllByteArrStart + Hll4ArrBytes(LgConfigK);

    /// <summary>
    /// The estimate. HIP when the sketch tracked every update itself, and the
    /// composite estimator otherwise.
    /// </summary>
    public override double Estimate => IsOutOfOrder ? CompositeEstimate : HipAccum;

    public override double CompositeEstimate => HllEstimators.CompositeEstimate(this);

    public override double HipEstimate => HipAccum;

    public override double GetLowerBound(int numStdDev)
    {
        HllUtil.CheckNumStdDev(numStdDev);
        return HllEstimators.LowerBound(this, numStdDev);
    }

    public override double GetUpperBound(int numStdDev)
    {
        HllUtil.CheckNumStdDev(numStdDev);
        return HllEstimators.UpperBound(this, numStdDev);
    }

    public override int CompactSerializationBytes =>
        HllPreamble.HllByteArrStart + HllByteArr.Length + (AuxHashMap?.CompactSizeBytes ?? 0);

    public override int UpdatableSerializationBytes
    {
        get
        {
            int auxBytes = TgtHllType == TgtHllType.Hll4
                ? AuxHashMap?.UpdatableSizeBytes ?? (4 << HllUtil.LgAuxArrInts[LgConfigK])
                : 0;
            return HllPreamble.HllByteArrStart + HllByteArr.Length + auxBytes;
        }
    }

    /// <summary>Reads the value of a register, resolving auxiliary entries.</summary>
    public abstract int GetSlotValue(int slotNo);

    /// <summary>Reads the raw four-bit nibble at a register. HLL_4 only.</summary>
    public virtual int GetNibble(int slotNo) =>
        throw new InvalidOperationException("Nibble access applies only to HLL_4.");

    /// <summary>Writes the raw four-bit nibble at a register. HLL_4 only.</summary>
    public virtual void PutNibble(int slotNo, int nibValue) =>
        throw new InvalidOperationException("Nibble access applies only to HLL_4.");

    /// <summary>
    /// Raises a register to <paramref name="newValue"/> if it is larger, keeping
    /// the running aggregates in step.
    /// </summary>
    public abstract void UpdateSlotWithKxQ(int slotNo, int newValue);

    public override HllSketchImpl CouponUpdate(int coupon)
    {
        int newValue = HllUtil.PairValue(coupon);
        int slotNo = coupon & ((1 << LgConfigK) - 1);
        UpdateSlotWithKxQ(slotNo, newValue);
        return this;
    }

    /// <summary>
    /// The compact form. For HLL_6 and HLL_8 this is byte-identical to the
    /// updatable form — there is no auxiliary table to shrink and the register
    /// array is fixed-size — so those types emit the updatable image, compact
    /// flag and all. Only <see cref="Hll4Array"/> overrides this.
    /// </summary>
    public override byte[] ToCompactByteArray() => ToUpdatableByteArray();

    public override byte[] ToUpdatableByteArray() => HllSerialization.HllToByteArray(this, compact: false);

    /// <summary>
    /// Raises a register without touching the running aggregates.
    /// </summary>
    /// <remarks>
    /// Used by the union's register-wise merge, which would otherwise pay the HIP
    /// and KxQ bookkeeping per register for aggregates it is about to invalidate
    /// anyway. The caller must set <see cref="RebuildCurMinNumKxQ"/> so they are
    /// recomputed before anyone reads an estimate.
    /// </remarks>
    public virtual void UpdateSlotNoKxQ(int slotNo, int newValue) =>
        throw new InvalidOperationException(
            "Register-wise merging is only supported into an HLL_8 array.");

    /// <inheritdoc/>
    public override HllSketchImpl CopyAs(TgtHllType tgtHllType)
    {
        if (tgtHllType == TgtHllType) { return Copy(); }
        return tgtHllType switch
        {
            TgtHllType.Hll4 => Conversions.ConvertToHll4(this),
            TgtHllType.Hll6 => Conversions.ConvertToHll6(this),
            _ => Conversions.ConvertToHll8(this),
        };
    }

    /// <inheritdoc/>
    public override void MergeTo(HllSketch target) =>
        throw new InvalidOperationException(
            "An HLL-mode sketch has no coupons to replay; merge its registers instead.");

    /// <summary>
    /// Writes every register's value into <paramref name="destination"/>, one
    /// byte each.
    /// </summary>
    /// <remarks>
    /// One virtual call for the whole array instead of one per register, which
    /// matters for the union: it walks every register on every merge. Subclasses
    /// that can unpack in bulk override this.
    /// </remarks>
    public virtual void DecodeRegisters(Span<byte> destination)
    {
        int configK = 1 << LgConfigK;
        for (int i = 0; i < configK; i++)
        {
            destination[i] = (byte)GetSlotValue(i);
        }
    }

    /// <summary>Enumerates the non-empty registers as (index, value) coupons.</summary>
    public IEnumerable<int> ValidPairs()
    {
        int configK = 1 << LgConfigK;
        for (int i = 0; i < configK; i++)
        {
            int value = GetSlotValue(i);
            if (value != HllUtil.Empty)
            {
                yield return HllUtil.Pair(i, value);
            }
        }
    }

    /// <summary>Enumerates every register value, including zeros.</summary>
    public IEnumerable<int> SlotValues()
    {
        int configK = 1 << LgConfigK;
        for (int i = 0; i < configK; i++)
        {
            yield return GetSlotValue(i);
        }
    }

    public static HllArray NewHeapHll(int lgConfigK, TgtHllType tgtHllType) => tgtHllType switch
    {
        TgtHllType.Hll4 => new Hll4Array(lgConfigK),
        TgtHllType.Hll6 => new Hll6Array(lgConfigK),
        _ => new Hll8Array(lgConfigK),
    };

    public static int Hll4ArrBytes(int lgConfigK) => 1 << (lgConfigK - 1);

    public static int Hll6ArrBytes(int lgConfigK) => (((1 << lgConfigK) * 3) >> 2) + 1;

    public static int Hll8ArrBytes(int lgConfigK) => 1 << lgConfigK;

    /// <summary>
    /// Advances the HIP accumulator and the KxQ registers for a register that is
    /// about to change from <paramref name="oldValue"/> to
    /// <paramref name="newValue"/>.
    /// </summary>
    /// <remarks>
    /// Order matters: HIP is incremented by <c>k/KxQ</c> using the KxQ values
    /// from <em>before</em> the change, because the increment is the inverse
    /// probability of the event that just occurred.
    /// </remarks>
    public static void HipAndKxQIncrementalUpdate(HllArray host, int oldValue, int newValue)
    {
        // Read before updating: the increment is the inverse probability of the
        // event that just happened, which is measured against the prior state.
        host.HipAccum += (1 << host.LgConfigK) / (host.KxQ0 + host.KxQ1);
        KxQIncrementalUpdate(host, oldValue, newValue);
    }

    /// <summary>
    /// The KxQ half of <see cref="HipAndKxQIncrementalUpdate"/>, without the HIP
    /// accumulation.
    /// </summary>
    /// <remarks>
    /// For callers that overwrite <see cref="HipAccum"/> afterwards — the
    /// register-width conversions do, from the source sketch — accumulating it
    /// per register is dead work, and it costs a floating-point division each
    /// time. The KxQ arithmetic here is the same operations in the same order,
    /// so the result is bit-for-bit what the full update would have left behind.
    /// </remarks>
    public static void KxQIncrementalUpdate(HllArray host, int oldValue, int newValue)
    {
        double kxq0 = host.KxQ0;
        double kxq1 = host.KxQ1;

        // Subtract the old contribution and add the new, each landing in whichever
        // register covers its magnitude.
        if (oldValue < 32) { host.KxQ0 = kxq0 -= HllUtil.InvPow2(oldValue); }
        else { host.KxQ1 = kxq1 -= HllUtil.InvPow2(oldValue); }

        if (newValue < 32) { host.KxQ0 = kxq0 + HllUtil.InvPow2(newValue); }
        else { host.KxQ1 = kxq1 + HllUtil.InvPow2(newValue); }
    }

    /// <summary>Loads the aggregates and register bytes from a serialized image.</summary>
    protected static void ExtractCommonHll(ReadOnlySpan<byte> image, HllArray array)
    {
        array.SetOutOfOrder(HllPreamble.ReadOutOfOrderFlag(image));
        array.CurMin = HllPreamble.ReadCurMin(image);
        array.HipAccum = HllPreamble.ReadHipAccum(image);
        array.KxQ0 = HllPreamble.ReadKxQ0(image);
        array.KxQ1 = HllPreamble.ReadKxQ1(image);
        array.NumAtCurMin = HllPreamble.ReadNumAtCurMin(image);
        array.RebuildCurMinNumKxQ = HllPreamble.ReadRebuildCurMinNumKxQFlag(image);

        int required = HllPreamble.HllByteArrStart + array.HllByteArr.Length;
        if (image.Length < required)
        {
            throw new InvalidDataException(
                $"Truncated HLL image: needs {required} bytes, got {image.Length}.");
        }

        image.Slice(HllPreamble.HllByteArrStart, array.HllByteArr.Length).CopyTo(array.HllByteArr);
    }
}
