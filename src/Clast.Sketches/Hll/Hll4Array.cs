// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// HLL registers packed four bits each, with a side table for the few that do
/// not fit. The most compact of the three, at about <c>k/2</c> bytes.
/// </summary>
/// <remarks>
/// <para>
/// Four bits cannot hold a register value outright, so each nibble stores the
/// value <em>relative to</em> <see cref="HllArray.CurMin"/>, the smallest value
/// any register currently holds. Because HLL register values cluster tightly,
/// almost every register fits in the resulting 0..14 range. The few that run
/// ahead store <see cref="HllUtil.AuxToken"/> and keep their real value in
/// <see cref="HllArray.AuxHashMap"/>.
/// </para>
/// <para>
/// As the sketch fills, the minimum rises and every nibble has to shift down —
/// see <see cref="Hll4Update"/>.
/// </para>
/// </remarks>
internal sealed class Hll4Array : HllArray
{
    public Hll4Array(int lgConfigK)
        : base(lgConfigK, TgtHllType.Hll4)
    {
        HllByteArr = new byte[Hll4ArrBytes(lgConfigK)];
    }

    private Hll4Array(Hll4Array other)
        : base(other)
    {
    }

    public override int GetNibble(int slotNo)
    {
        int theByte = HllByteArr[slotNo >> 1];
        if ((slotNo & 1) > 0)
        {
            theByte >>= 4;
        }
        return theByte & HllUtil.LoNibbleMask;
    }

    public override void PutNibble(int slotNo, int nibValue)
    {
        int byteNo = slotNo >> 1;
        int oldValue = HllByteArr[byteNo];
        HllByteArr[byteNo] = (slotNo & 1) == 0
            ? (byte)((oldValue & HllUtil.HiNibbleMask) | (nibValue & HllUtil.LoNibbleMask))
            : (byte)((oldValue & HllUtil.LoNibbleMask) | ((nibValue << 4) & HllUtil.HiNibbleMask));
    }

    public override int GetSlotValue(int slotNo)
    {
        int nib = GetNibble(slotNo);
        if (nib == HllUtil.AuxToken)
        {
            return AuxHashMap!.MustFindValueFor(slotNo);
        }
        return nib + CurMin;
    }

    public override void UpdateSlotWithKxQ(int slotNo, int newValue) =>
        Hll4Update.InternalUpdate(this, slotNo, newValue);

    /// <inheritdoc/>
    /// <remarks>
    /// Expands every nibble in bulk, then corrects the exceptions. The bulk pass
    /// gives <c>15 + curMin</c> for a register whose real value lives in the
    /// auxiliary table, and the table holds precisely those slots — so patching
    /// from it costs one write per exception rather than a branch per register.
    /// </remarks>
    public override void DecodeRegisters(Span<byte> destination)
    {
        HllRegisters.ExpandNibbles(HllByteArr, CurMin, destination);

        if (AuxHashMap is null)
        {
            return;
        }

        int configKmask = (1 << LgConfigK) - 1;
        foreach (int pair in AuxHashMap.Pairs())
        {
            destination[HllUtil.PairLow26(pair) & configKmask] = (byte)HllUtil.PairValue(pair);
        }
    }

    /// <summary>
    /// Creates the auxiliary table on first need, sized from the tabulated
    /// initial size for this lgK.
    /// </summary>
    public AuxHashMap NewAuxHashMap() => new(HllUtil.LgAuxArrInts[LgConfigK], LgConfigK);

    /// <summary>
    /// HLL_4 is the one type whose compact form differs: the auxiliary table is
    /// written as just its occupied entries rather than the whole hash table.
    /// </summary>
    public override byte[] ToCompactByteArray() => HllSerialization.HllToByteArray(this, compact: true);

    public override HllSketchImpl Copy() => new Hll4Array(this);

    public static Hll4Array Deserialize(ReadOnlySpan<byte> image)
    {
        var array = new Hll4Array(HllPreamble.ReadLgK(image));
        ExtractCommonHll(image, array);

        int auxCount = HllPreamble.ReadAuxCount(image);
        if (auxCount > 0)
        {
            array.AuxHashMap = AuxHashMap.Deserialize(
                image, array.AuxStart, array.LgConfigK, auxCount, HllPreamble.ReadCompactFlag(image));
        }

        return array;
    }
}
