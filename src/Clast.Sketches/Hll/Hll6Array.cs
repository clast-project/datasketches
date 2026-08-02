// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Hll;

/// <summary>
/// HLL registers packed six bits each. Three quarters the size of
/// <see cref="Hll8Array"/>, with no auxiliary table to manage.
/// </summary>
/// <remarks>
/// Six bits is exactly what a register value needs, so unlike HLL_4 nothing ever
/// overflows. A register straddles a byte boundary, so reads and writes go
/// through a 16-bit window — which is why the backing array carries one extra
/// byte, so a read of the last register cannot run off the end.
/// </remarks>
internal sealed class Hll6Array : HllArray
{
    public Hll6Array(int lgConfigK)
        : base(lgConfigK, TgtHllType.Hll6)
    {
        HllByteArr = new byte[Hll6ArrBytes(lgConfigK)];
    }

    private Hll6Array(Hll6Array other)
        : base(other)
    {
    }

    public override int GetSlotValue(int slotNo) => Get6Bit(HllByteArr, slotNo);

    public override void UpdateSlotWithKxQ(int slotNo, int newValue)
    {
        int oldValue = GetSlotValue(slotNo);
        if (newValue <= oldValue)
        {
            return;
        }

        Put6Bit(HllByteArr, slotNo, newValue);
        HipAndKxQIncrementalUpdate(this, oldValue, newValue);

        if (oldValue == 0)
        {
            NumAtCurMin--;
        }
    }

    public override HllSketchImpl Copy() => new Hll6Array(this);

    public static Hll6Array Deserialize(ReadOnlySpan<byte> image)
    {
        var array = new Hll6Array(HllPreamble.ReadLgK(image));
        ExtractCommonHll(image, array);
        return array;
    }

    private static int Get6Bit(byte[] arr, int slotNo)
    {
        int startBit = slotNo * 6;
        int shift = startBit & 0x7;
        int byteIdx = startBit >> 3;
        return (BinaryPrimitives.ReadInt16LittleEndian(arr.AsSpan(byteIdx)) >> shift) & 0x3F;
    }

    private static void Put6Bit(byte[] arr, int slotNo, int newValue)
    {
        int startBit = slotNo * 6;
        int shift = startBit & 0x7;
        int byteIdx = startBit >> 3;

        int current = BinaryPrimitives.ReadInt16LittleEndian(arr.AsSpan(byteIdx));
        int cleared = current & ~(HllUtil.ValMask6 << shift);
        int inserted = cleared | ((newValue & 0x3F) << shift);
        BinaryPrimitives.WriteInt16LittleEndian(arr.AsSpan(byteIdx), (short)inserted);
    }
}
