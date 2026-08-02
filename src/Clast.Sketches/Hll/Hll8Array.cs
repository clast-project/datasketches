// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// HLL registers stored one per byte. Largest, and the fastest to update.
/// </summary>
internal sealed class Hll8Array : HllArray
{
    public Hll8Array(int lgConfigK)
        : base(lgConfigK, TgtHllType.Hll8)
    {
        HllByteArr = new byte[Hll8ArrBytes(lgConfigK)];
    }

    private Hll8Array(Hll8Array other)
        : base(other)
    {
    }

    public override int GetSlotValue(int slotNo) => HllByteArr[slotNo] & HllUtil.ValMask6;

    public override void UpdateSlotWithKxQ(int slotNo, int newValue)
    {
        int oldValue = GetSlotValue(slotNo);
        if (newValue <= oldValue)
        {
            return;
        }

        HllByteArr[slotNo] = (byte)(newValue & HllUtil.ValMask6);
        HipAndKxQIncrementalUpdate(this, oldValue, newValue);

        if (oldValue == 0)
        {
            // CurMin stays zero for this type, so NumAtCurMin is simply the count
            // of registers still untouched.
            NumAtCurMin--;
        }
    }

    /// <inheritdoc/>
    public override void UpdateSlotNoKxQ(int slotNo, int newValue)
    {
        int oldValue = GetSlotValue(slotNo);
        HllByteArr[slotNo] = (byte)Math.Max(newValue, oldValue);
    }

    public override HllSketchImpl Copy() => new Hll8Array(this);

    public static Hll8Array Deserialize(ReadOnlySpan<byte> image)
    {
        var array = new Hll8Array(HllPreamble.ReadLgK(image));
        ExtractCommonHll(image, array);
        return array;
    }
}
