// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// The two early modes of an HLL sketch, which store coupons directly rather
/// than allocating the register array.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HllCurMode.List"/> is a short unsorted array scanned linearly;
/// <see cref="HllCurMode.Set"/> is an open-addressed hash table. Both hold the
/// same 32-bit coupons, so promotion between them and on into HLL mode is just a
/// replay.
/// </para>
/// <para>
/// The point is size: a sketch with a handful of distinct values costs a handful
/// of bytes instead of the full <c>k</c> registers. The estimate over this range
/// is also better than HLL's, since the coupons are exact.
/// </para>
/// </remarks>
internal sealed class CouponList : HllSketchImpl
{
    private int[] _couponIntArr;
    private int _lgCouponArrInts;
    private int _couponCount;

    public CouponList(int lgConfigK, TgtHllType tgtHllType, HllCurMode curMode)
        : base(lgConfigK, tgtHllType, curMode)
    {
        _lgCouponArrInts = curMode == HllCurMode.List ? HllUtil.LgInitListSize : HllUtil.LgInitSetSize;
        _couponIntArr = new int[1 << _lgCouponArrInts];
        _couponCount = 0;
    }

    private CouponList(CouponList other)
        : base(other.LgConfigK, other.TgtHllType, other.CurMode)
    {
        _lgCouponArrInts = other._lgCouponArrInts;
        _couponCount = other._couponCount;
        _couponIntArr = (int[])other._couponIntArr.Clone();
    }

    public int CouponCount => _couponCount;

    public int LgCouponArrInts => _lgCouponArrInts;

    public int[] CouponIntArr => _couponIntArr;

    public override bool IsEmpty => _couponCount == 0;

    /// <summary>Coupon modes are exact, so ordering never matters to them.</summary>
    public override bool IsOutOfOrder => false;

    public override int PreInts =>
        CurMode == HllCurMode.List ? HllPreamble.ListPreInts : HllPreamble.HashSetPreInts;

    public override int DataStart =>
        CurMode == HllCurMode.List ? HllPreamble.ListIntArrStart : HllPreamble.HashSetIntArrStart;

    public override int CompactSerializationBytes => DataStart + (_couponCount << 2);

    public override int UpdatableSerializationBytes => DataStart + (4 << _lgCouponArrInts);

    /// <summary>
    /// Estimated distinct count, interpolated from the coupon count.
    /// </summary>
    /// <remarks>
    /// Floored at the coupon count: every coupon came from at least one distinct
    /// value, so the estimate can never legitimately fall below it.
    /// </remarks>
    public override double Estimate =>
        Math.Max(
            CubicInterpolation.UsingXAndYTables(CouponMapping.XArr, CouponMapping.YArr, _couponCount),
            _couponCount);

    public override double CompositeEstimate => Estimate;

    public override double HipEstimate => Estimate;

    public override double GetLowerBound(int numStdDev)
    {
        HllUtil.CheckNumStdDev(numStdDev);
        double est = CubicInterpolation.UsingXAndYTables(CouponMapping.XArr, CouponMapping.YArr, _couponCount);
        return Math.Max(est / (1.0 + (numStdDev * HllUtil.CouponRse)), _couponCount);
    }

    public override double GetUpperBound(int numStdDev)
    {
        HllUtil.CheckNumStdDev(numStdDev);
        double est = CubicInterpolation.UsingXAndYTables(CouponMapping.XArr, CouponMapping.YArr, _couponCount);
        return Math.Max(est / (1.0 - (numStdDev * HllUtil.CouponRse)), _couponCount);
    }

    public override HllSketchImpl CouponUpdate(int coupon) =>
        CurMode == HllCurMode.List ? ListUpdate(coupon) : SetUpdate(coupon);

    /// <summary>
    /// List mode: scan for the coupon or the first gap. Linear, but the list is
    /// only eight entries before it promotes.
    /// </summary>
    private HllSketchImpl ListUpdate(int coupon)
    {
        int len = 1 << _lgCouponArrInts;
        for (int i = 0; i < len; i++)
        {
            int couponAtIdx = _couponIntArr[i];
            if (couponAtIdx == HllUtil.Empty)
            {
                _couponIntArr[i] = coupon;
                _couponCount++;
                if (_couponCount >= len)
                {
                    // A tiny sketch has fewer registers than the set mode needs,
                    // so it skips straight to HLL.
                    return LgConfigK < 8 ? PromoteToHll(this) : PromoteListToSet(this);
                }
                return this;
            }
            if (couponAtIdx == coupon)
            {
                return this;
            }
        }

        throw new InvalidOperationException("Coupon list is full with no duplicate found.");
    }

    private HllSketchImpl SetUpdate(int coupon)
    {
        int index = Find(_couponIntArr, _lgCouponArrInts, coupon);
        if (index >= 0)
        {
            return this;
        }

        _couponIntArr[~index] = coupon;
        _couponCount++;

        if (HllUtil.ResizeDenom * _couponCount > HllUtil.ResizeNumer * (1 << _lgCouponArrInts))
        {
            if (_lgCouponArrInts == LgConfigK - 3)
            {
                // The set has grown to the point where the register array is the
                // cheaper representation.
                return PromoteToHll(this);
            }
            GrowHashSet();
        }

        return this;
    }

    private void GrowHashSet()
    {
        int[] oldArr = _couponIntArr;
        _lgCouponArrInts++;
        _couponIntArr = new int[1 << _lgCouponArrInts];

        foreach (int fetched in oldArr)
        {
            if (fetched == HllUtil.Empty) { continue; }
            int index = Find(_couponIntArr, _lgCouponArrInts, fetched);
            if (index >= 0)
            {
                throw new InvalidOperationException("Duplicate coupon found while growing the hash set.");
            }
            _couponIntArr[~index] = fetched;
        }
    }

    public override HllSketchImpl Copy() => new CouponList(this);

    public override byte[] ToCompactByteArray() => HllSerialization.CouponsToByteArray(this, compact: true);

    public override byte[] ToUpdatableByteArray() => HllSerialization.CouponsToByteArray(this, compact: false);

    /// <summary>Enumerates the stored coupons, skipping empty slots.</summary>
    public IEnumerable<int> ValidCoupons()
    {
        foreach (int coupon in _couponIntArr)
        {
            if (coupon != HllUtil.Empty)
            {
                yield return coupon;
            }
        }
    }

    /// <summary>Replays a full list into a hash set.</summary>
    private static HllSketchImpl PromoteListToSet(CouponList list)
    {
        var set = new CouponList(list.LgConfigK, list.TgtHllType, HllCurMode.Set);
        for (int i = 0; i < list._couponCount; i++)
        {
            set.CouponUpdate(list._couponIntArr[i]);
        }
        return set;
    }

    /// <summary>
    /// Replays coupons into a freshly allocated register array.
    /// </summary>
    /// <remarks>
    /// The HIP accumulator is seeded from the coupon-mode estimate rather than
    /// accumulated during the replay, because the replay is not the order the
    /// values actually arrived in. That seeding is what lets the sketch keep
    /// using the HIP estimator afterwards.
    /// </remarks>
    public static HllSketchImpl PromoteToHll(CouponList src)
    {
        HllArray target = HllArray.NewHeapHll(src.LgConfigK, src.TgtHllType);
        target.KxQ0 = 1 << src.LgConfigK;

        foreach (int coupon in src.ValidCoupons())
        {
            target.CouponUpdate(coupon);
        }

        target.HipAccum = src.Estimate;
        target.SetOutOfOrder(false);
        return target;
    }

    /// <summary>
    /// Rebuilds a coupon list or set from a serialized image.
    /// </summary>
    public static CouponList Deserialize(ReadOnlySpan<byte> image, HllCurMode curMode)
    {
        int lgConfigK = HllPreamble.ReadLgK(image);
        TgtHllType tgtHllType = HllPreamble.ReadTgtHllType(image);

        if (curMode == HllCurMode.List)
        {
            var list = new CouponList(lgConfigK, tgtHllType, HllCurMode.List);
            int count = HllPreamble.ReadListCount(image);
            if (count > list._couponIntArr.Length)
            {
                throw new InvalidDataException($"Corrupt image: list count {count} exceeds capacity.");
            }
            for (int i = 0; i < count; i++)
            {
                list._couponIntArr[i] = HllPreamble.ReadInt(image, HllPreamble.ListIntArrStart + (i << 2));
            }
            list._couponCount = count;
            return list;
        }

        var set = new CouponList(lgConfigK, tgtHllType, HllCurMode.Set);
        int couponCount = HllPreamble.ReadHashSetCount(image);
        bool compact = HllPreamble.ReadCompactFlag(image);

        if (compact)
        {
            // Only the occupied entries are stored, so replay them to rebuild the
            // table; probe positions depend on the table size, not the file order.
            for (int i = 0; i < couponCount; i++)
            {
                set.CouponUpdate(HllPreamble.ReadInt(image, HllPreamble.HashSetIntArrStart + (i << 2)));
            }
            return set;
        }

        int lgCouponArrInts = HllPreamble.ReadLgArr(image);
        if (lgCouponArrInts < HllUtil.LgInitSetSize)
        {
            lgCouponArrInts = HllPreamble.ComputeLgArr(image, couponCount, lgConfigK);
        }

        set._lgCouponArrInts = lgCouponArrInts;
        set._couponCount = couponCount;
        set._couponIntArr = new int[1 << lgCouponArrInts];
        for (int i = 0; i < set._couponIntArr.Length; i++)
        {
            set._couponIntArr[i] = HllPreamble.ReadInt(image, HllPreamble.HashSetIntArrStart + (i << 2));
        }
        return set;
    }

    /// <summary>
    /// Open-addressing probe over the coupon table.
    /// </summary>
    /// <returns>
    /// The index of a matching coupon, or the bitwise complement of the first
    /// empty slot's index if it is absent.
    /// </returns>
    public static int Find(int[] array, int lgArrInts, int coupon)
    {
        int arrMask = array.Length - 1;
        int probe = coupon & arrMask;
        int loopIndex = probe;

        do
        {
            int couponAtIdx = array[probe];
            if (couponAtIdx == HllUtil.Empty)
            {
                return ~probe;
            }
            if (couponAtIdx == coupon)
            {
                return probe;
            }
            int stride = ((int)((uint)(coupon & HllUtil.KeyMask26) >> lgArrInts)) | 1;
            probe = (probe + stride) & arrMask;
        }
        while (probe != loopIndex);

        throw new InvalidOperationException("Coupon table is full and the coupon was not found.");
    }
}
