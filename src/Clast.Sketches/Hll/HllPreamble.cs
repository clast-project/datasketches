// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Hll;

/// <summary>
/// Byte-level layout of the HLL sketch preamble.
/// </summary>
/// <remarks>
/// <para>
/// Three layouts, one per mode, distinguished by the preamble length in ints:
/// 2 for LIST, 3 for SET, 10 for HLL. Everything is little-endian.
/// </para>
/// <code>
/// bytes 0..7   preInts | serVer | famId | lgK | lgArr | flags | listCount/curMin | mode
/// LIST         then the coupon ints from byte 8
/// SET          count at byte 8, then the coupon ints from byte 12
/// HLL          hipAccum at 8, kxq0 at 16, kxq1 at 24, numAtCurMin at 32,
///              auxCount at 36, then the register array from byte 40
/// </code>
/// </remarks>
internal static class HllPreamble
{
    public const int PreambleIntsByte = 0;
    public const int SerVerByte = 1;
    public const int FamilyByte = 2;
    public const int LgKByte = 3;
    public const int LgArrByte = 4;
    public const int FlagsByte = 5;

    /// <summary>Coupon count in LIST mode; the register minimum in HLL mode.</summary>
    public const int ListCountByte = 6;
    public const int HllCurMinByte = 6;

    /// <summary>Low two bits are the mode, next two the target type.</summary>
    public const int ModeByte = 7;

    public const int ListIntArrStart = 8;
    public const int HashSetCountInt = 8;
    public const int HashSetIntArrStart = 12;

    public const int HipAccumDouble = 8;
    public const int KxQ0Double = 16;
    public const int KxQ1Double = 24;
    public const int CurMinCountInt = 32;
    public const int AuxCountInt = 36;
    public const int HllByteArrStart = 40;

    public const int ReservedFlagMask = 1;
    public const int ReadOnlyFlagMask = 2;
    public const int EmptyFlagMask = 4;
    public const int CompactFlagMask = 8;
    public const int OutOfOrderFlagMask = 16;
    public const int RebuildCurMinNumKxQMask = 32;

    public const int CurModeMask = 3;
    public const int TgtHllTypeMask = 12;

    public const int SerVer = 1;
    public const int FamilyId = 7;

    public const int ListPreInts = 2;
    public const int HashSetPreInts = 3;
    public const int HllPreInts = 10;

    public static int ReadPreInts(ReadOnlySpan<byte> image) => image[PreambleIntsByte] & 0x3F;

    public static int ReadSerVer(ReadOnlySpan<byte> image) => image[SerVerByte];

    public static int ReadFamilyId(ReadOnlySpan<byte> image) => image[FamilyByte];

    public static int ReadLgK(ReadOnlySpan<byte> image) => image[LgKByte];

    public static int ReadLgArr(ReadOnlySpan<byte> image) => image[LgArrByte];

    public static int ReadFlags(ReadOnlySpan<byte> image) => image[FlagsByte];

    public static int ReadListCount(ReadOnlySpan<byte> image) => image[ListCountByte];

    public static int ReadCurMin(ReadOnlySpan<byte> image) => image[HllCurMinByte];

    public static HllCurMode ReadCurMode(ReadOnlySpan<byte> image) =>
        (HllCurMode)(image[ModeByte] & CurModeMask);

    public static TgtHllType ReadTgtHllType(ReadOnlySpan<byte> image) =>
        (TgtHllType)((image[ModeByte] & TgtHllTypeMask) >> 2);

    public static bool ReadEmptyFlag(ReadOnlySpan<byte> image) =>
        (image[FlagsByte] & EmptyFlagMask) != 0;

    public static bool ReadCompactFlag(ReadOnlySpan<byte> image) =>
        (image[FlagsByte] & CompactFlagMask) != 0;

    public static bool ReadOutOfOrderFlag(ReadOnlySpan<byte> image) =>
        (image[FlagsByte] & OutOfOrderFlagMask) != 0;

    public static bool ReadRebuildCurMinNumKxQFlag(ReadOnlySpan<byte> image) =>
        (image[FlagsByte] & RebuildCurMinNumKxQMask) != 0;

    public static int ReadHashSetCount(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadInt32LittleEndian(image.Slice(HashSetCountInt));

    public static double ReadHipAccum(ReadOnlySpan<byte> image) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(image.Slice(HipAccumDouble)));

    public static double ReadKxQ0(ReadOnlySpan<byte> image) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(image.Slice(KxQ0Double)));

    public static double ReadKxQ1(ReadOnlySpan<byte> image) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(image.Slice(KxQ1Double)));

    public static int ReadNumAtCurMin(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadInt32LittleEndian(image.Slice(CurMinCountInt));

    public static int ReadAuxCount(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadInt32LittleEndian(image.Slice(AuxCountInt));

    public static int ReadInt(ReadOnlySpan<byte> image, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(image.Slice(offset));

    public static void WriteInt(Span<byte> image, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(image.Slice(offset), value);

    public static void WriteDouble(Span<byte> image, int offset, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(image.Slice(offset), BitConverter.DoubleToInt64Bits(value));

    public static void WriteCommonHeader(
        Span<byte> image, int preInts, int lgK, HllCurMode curMode, TgtHllType tgtHllType)
    {
        image[PreambleIntsByte] = (byte)(preInts & 0x3F);
        image[SerVerByte] = SerVer;
        image[FamilyByte] = FamilyId;
        image[LgKByte] = (byte)lgK;
        image[ModeByte] = (byte)(((int)curMode & CurModeMask) | (((int)tgtHllType << 2) & TgtHllTypeMask));
    }

    public static void SetFlag(Span<byte> image, int mask, bool value)
    {
        if (value)
        {
            image[FlagsByte] |= (byte)mask;
        }
        else
        {
            image[FlagsByte] &= unchecked((byte)~mask);
        }
    }

    /// <summary>
    /// Recovers the coupon table size for images that predate the
    /// <see cref="LgArrByte"/> field, by inferring the size the writer must have
    /// used for that count.
    /// </summary>
    public static int ComputeLgArr(ReadOnlySpan<byte> image, int count, int lgConfigK)
    {
        HllCurMode curMode = ReadCurMode(image);
        if (curMode == HllCurMode.List)
        {
            return HllUtil.LgInitListSize;
        }

        int ceilPow2 = HllUtil.CeilingPowerOf2(count);
        if (HllUtil.ResizeDenom * count > HllUtil.ResizeNumer * ceilPow2)
        {
            ceilPow2 <<= 1;
        }

        return curMode == HllCurMode.Set
            ? Math.Max(HllUtil.LgInitSetSize, HllUtil.ExactLog2(ceilPow2))
            : Math.Max(HllUtil.LgAuxArrInts[lgConfigK], HllUtil.ExactLog2(ceilPow2));
    }

    /// <summary>Validates the preamble and returns the mode it declares.</summary>
    public static HllCurMode CheckPreamble(ReadOnlySpan<byte> image)
    {
        if (image.Length < 8)
        {
            throw new InvalidDataException($"An HLL image needs at least 8 bytes, got {image.Length}.");
        }

        int preInts = ReadPreInts(image);
        if (image.Length < preInts * 4)
        {
            throw new InvalidDataException(
                $"Truncated image: a {preInts}-int preamble needs {preInts * 4} bytes, got {image.Length}.");
        }

        int serVer = ReadSerVer(image);
        int familyId = ReadFamilyId(image);
        HllCurMode curMode = ReadCurMode(image);

        int expectedPreInts = curMode switch
        {
            HllCurMode.List => ListPreInts,
            HllCurMode.Set => HashSetPreInts,
            _ => HllPreInts,
        };

        if (familyId != FamilyId || serVer != SerVer || preInts != expectedPreInts)
        {
            throw new InvalidDataException(
                $"Corrupt HLL preamble: family {familyId} (expected {FamilyId}), " +
                $"serVer {serVer} (expected {SerVer}), preInts {preInts} (expected {expectedPreInts} for {curMode}).");
        }

        return curMode;
    }
}
