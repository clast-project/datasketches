// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.hll.Conversions from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// Converts an HLL-mode sketch between register widths.
/// </summary>
/// <remarks>
/// The three widths are isomorphic, so a conversion is lossless and the
/// estimate is unchanged. The HIP accumulator is copied across rather than
/// recomputed, since it records history the register values no longer carry.
/// </remarks>
internal static class Conversions
{
    public static Hll4Array ConvertToHll4(HllArray src)
    {
        int lgConfigK = src.LgConfigK;
        int configK = 1 << lgConfigK;
        var target = new Hll4Array(lgConfigK);
        target.SetOutOfOrder(src.IsOutOfOrder);

        // Two passes: the four-bit encoding is relative to the minimum, so the
        // minimum has to be known before any register can be written.
        (int curMin, int numAtCurMin) = CurMinAndNum(src);

        AuxHashMap? auxHashMap = null;
        for (int slotNo = 0; slotNo < configK; slotNo++)
        {
            int actualValue = src.GetSlotValue(slotNo);
            if (actualValue == HllUtil.Empty)
            {
                continue;
            }

            HllArray.KxQIncrementalUpdate(target, 0, actualValue);

            if (actualValue >= curMin + 15)
            {
                target.PutNibble(slotNo, HllUtil.AuxToken);
                auxHashMap ??= target.NewAuxHashMap();
                auxHashMap.MustAdd(slotNo, actualValue);
            }
            else
            {
                target.PutNibble(slotNo, actualValue - curMin);
            }
        }

        target.AuxHashMap = auxHashMap;
        target.CurMin = curMin;
        target.NumAtCurMin = numAtCurMin;
        // Deliberately overwrites the value the replay above accumulated: HIP is
        // a property of the update history, not of the register contents.
        target.HipAccum = src.HipAccum;
        target.RebuildCurMinNumKxQ = false;
        return target;
    }

    public static Hll6Array ConvertToHll6(HllArray src) =>
        (Hll6Array)ConvertToWide(src, new Hll6Array(src.LgConfigK));

    public static Hll8Array ConvertToHll8(HllArray src) =>
        (Hll8Array)ConvertToWide(src, new Hll8Array(src.LgConfigK));

    /// <summary>
    /// Replays registers into a six- or eight-bit target, which need no minimum
    /// and so no first pass.
    /// </summary>
    private static HllArray ConvertToWide(HllArray src, HllArray target)
    {
        target.SetOutOfOrder(src.IsOutOfOrder);

        int configK = 1 << src.LgConfigK;
        int numZeros = configK;

        // The target is fresh, so every register goes from 0 exactly once. That
        // makes the coupon path's extra bookkeeping redundant: its NumAtCurMin
        // decrement is overwritten below, and its HIP accumulation is overwritten
        // from the source. Only KxQ has to be accumulated, and in register order.
        if (target is Hll8Array target8)
        {
            // Fused rather than a bulk decode followed by a KxQ pass: splitting
            // them measured slower, because the second pass re-reads a register
            // array that no longer fits in L1.
            byte[] registers = target8.ByteArray;
            for (int slotNo = 0; slotNo < configK; slotNo++)
            {
                int value = src.GetSlotValue(slotNo);
                if (value == HllUtil.Empty)
                {
                    continue;
                }
                numZeros--;
                registers[slotNo] = (byte)value;
                HllArray.KxQIncrementalUpdate(target8, 0, value);
            }
        }
        else
        {
            for (int slotNo = 0; slotNo < configK; slotNo++)
            {
                int value = src.GetSlotValue(slotNo);
                if (value == HllUtil.Empty)
                {
                    continue;
                }
                numZeros--;
                target.CouponUpdate(HllUtil.Pair(slotNo, value));
            }
        }

        target.NumAtCurMin = numZeros;
        target.HipAccum = src.HipAccum;
        target.RebuildCurMinNumKxQ = false;
        return target;
    }

    /// <summary>
    /// The smallest register value and how many registers hold it.
    /// </summary>
    /// <remarks>
    /// Correct for any register width. For HLL_6 and HLL_8 the minimum is always
    /// zero until every register is filled, so this usually returns the count of
    /// empty registers.
    /// </remarks>
    public static (int CurMin, int NumAtCurMin) CurMinAndNum(HllArray array)
    {
        int curMin = 64;
        int numAtCurMin = 0;
        int configK = 1 << array.LgConfigK;

        for (int slotNo = 0; slotNo < configK; slotNo++)
        {
            int v = array.GetSlotValue(slotNo);
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

        return (curMin, numAtCurMin);
    }
}
