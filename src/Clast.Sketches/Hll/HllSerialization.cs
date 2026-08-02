// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// Writes HLL sketches to their DataSketches-compatible images.
/// </summary>
/// <remarks>
/// Every mode has a compact and an updatable form. Compact stores only the
/// occupied entries and is what you persist; updatable stores whole tables so
/// the image can be resumed without a rebuild. They differ only in the coupon
/// and auxiliary sections — the preamble is the same either way, distinguished
/// by the compact flag.
/// </remarks>
internal static class HllSerialization
{
    /// <summary>Serializes a LIST- or SET-mode sketch.</summary>
    public static byte[] CouponsToByteArray(CouponList list, bool compact)
    {
        int dataStart = list.DataStart;
        bool isList = list.CurMode == HllCurMode.List;

        int bytesOut = compact
            ? dataStart + (list.CouponCount << 2)
            : dataStart + (4 << list.LgCouponArrInts);

        byte[] image = new byte[bytesOut];
        WriteCouponHeader(list, image, compact);

        if (compact)
        {
            int count = 0;
            foreach (int coupon in list.ValidCoupons())
            {
                HllPreamble.WriteInt(image, dataStart + (count++ << 2), coupon);
            }
        }
        else
        {
            int[] arr = list.CouponIntArr;
            for (int i = 0; i < arr.Length; i++)
            {
                HllPreamble.WriteInt(image, dataStart + (i << 2), arr[i]);
            }
        }

        if (isList)
        {
            image[HllPreamble.ListCountByte] = (byte)list.CouponCount;
        }
        else
        {
            HllPreamble.WriteInt(image, HllPreamble.HashSetCountInt, list.CouponCount);
        }

        return image;
    }

    /// <summary>Serializes an HLL-mode sketch.</summary>
    public static byte[] HllToByteArray(HllArray array, bool compact)
    {
        int auxBytes = 0;
        if (array.TgtHllType == TgtHllType.Hll4)
        {
            AuxHashMap? aux = array.AuxHashMap;
            auxBytes = aux is not null
                ? (compact ? aux.CompactSizeBytes : aux.UpdatableSizeBytes)
                // An updatable image reserves the auxiliary table even when it is
                // empty, so the reader can grow it in place.
                : (compact ? 0 : 4 << HllUtil.LgAuxArrInts[array.LgConfigK]);
        }

        byte[] image = new byte[HllPreamble.HllByteArrStart + array.ByteArray.Length + auxBytes];

        HllPreamble.WriteCommonHeader(image, array.PreInts, array.LgConfigK, array.CurMode, array.TgtHllType);
        HllPreamble.SetFlag(image, HllPreamble.EmptyFlagMask, array.IsEmpty);
        HllPreamble.SetFlag(image, HllPreamble.CompactFlagMask, compact);
        HllPreamble.SetFlag(image, HllPreamble.OutOfOrderFlagMask, array.IsOutOfOrder);
        HllPreamble.SetFlag(image, HllPreamble.RebuildCurMinNumKxQMask, array.RebuildCurMinNumKxQ);
        image[HllPreamble.HllCurMinByte] = (byte)array.CurMin;

        HllPreamble.WriteDouble(image, HllPreamble.HipAccumDouble, array.HipAccum);
        HllPreamble.WriteDouble(image, HllPreamble.KxQ0Double, array.KxQ0);
        HllPreamble.WriteDouble(image, HllPreamble.KxQ1Double, array.KxQ1);
        HllPreamble.WriteInt(image, HllPreamble.CurMinCountInt, array.NumAtCurMin);

        array.ByteArray.CopyTo(image, HllPreamble.HllByteArrStart);

        AuxHashMap? auxHashMap = array.AuxHashMap;
        if (auxHashMap is null)
        {
            HllPreamble.WriteInt(image, HllPreamble.AuxCountInt, 0);
            return image;
        }

        HllPreamble.WriteInt(image, HllPreamble.AuxCountInt, auxHashMap.AuxCount);
        // Recorded for the updatable form's benefit; harmless in a compact image.
        image[HllPreamble.LgArrByte] = (byte)auxHashMap.LgAuxArrInts;

        int auxStart = array.AuxStart;
        if (compact)
        {
            int count = 0;
            foreach (int pair in auxHashMap.Pairs())
            {
                HllPreamble.WriteInt(image, auxStart + (count++ << 2), pair);
            }
        }
        else
        {
            int[] auxArr = auxHashMap.AuxIntArr;
            for (int i = 0; i < auxArr.Length; i++)
            {
                HllPreamble.WriteInt(image, auxStart + (i << 2), auxArr[i]);
            }
        }

        return image;
    }

    private static void WriteCouponHeader(CouponList list, Span<byte> image, bool compact)
    {
        HllPreamble.WriteCommonHeader(image, list.PreInts, list.LgConfigK, list.CurMode, list.TgtHllType);
        image[HllPreamble.LgArrByte] = (byte)list.LgCouponArrInts;
        HllPreamble.SetFlag(image, HllPreamble.EmptyFlagMask, list.IsEmpty);
        HllPreamble.SetFlag(image, HllPreamble.CompactFlagMask, compact);
        HllPreamble.SetFlag(image, HllPreamble.OutOfOrderFlagMask, list.IsOutOfOrder);
    }

    /// <summary>Reads any HLL sketch image, in any mode or form.</summary>
    public static HllSketchImpl Deserialize(ReadOnlySpan<byte> image)
    {
        HllCurMode curMode = HllPreamble.CheckPreamble(image);
        int lgK = HllUtil.CheckLgK(HllPreamble.ReadLgK(image));

        if (curMode != HllCurMode.Hll)
        {
            return CouponList.Deserialize(image, curMode);
        }

        return HllPreamble.ReadTgtHllType(image) switch
        {
            TgtHllType.Hll4 => Hll4Array.Deserialize(image),
            TgtHllType.Hll6 => Hll6Array.Deserialize(image),
            TgtHllType.Hll8 => Hll8Array.Deserialize(image),
            var other => throw new InvalidDataException($"Unrecognized HLL target type {other}."),
        };
    }
}
