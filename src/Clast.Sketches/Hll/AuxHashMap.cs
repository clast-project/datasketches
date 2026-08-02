// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// Side table holding the register values that will not fit in four bits.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="TgtHllType.Hll4"/> needs this. Its registers store a value
/// relative to the array's running minimum, which keeps almost every register
/// inside four bits — but a few outliers always run ahead. Those registers store
/// <see cref="HllUtil.AuxToken"/> instead and their real value lives here, keyed
/// by register number.
/// </para>
/// <para>
/// Exceptions are rare, so this stays small: about 3% of the sketch at
/// <c>lgK &gt; 13</c>.
/// </para>
/// </remarks>
internal sealed class AuxHashMap
{
    private readonly int _lgConfigK;

    private int[] _auxIntArr;
    private int _lgAuxArrInts;
    private int _auxCount;

    public AuxHashMap(int lgAuxArrInts, int lgConfigK)
    {
        _lgConfigK = lgConfigK;
        _lgAuxArrInts = lgAuxArrInts;
        _auxIntArr = new int[1 << lgAuxArrInts];
    }

    private AuxHashMap(AuxHashMap other)
    {
        _lgConfigK = other._lgConfigK;
        _lgAuxArrInts = other._lgAuxArrInts;
        _auxCount = other._auxCount;
        _auxIntArr = (int[])other._auxIntArr.Clone();
    }

    /// <summary>Number of exceptions stored.</summary>
    public int AuxCount => _auxCount;

    /// <summary>Base-2 logarithm of the table length.</summary>
    public int LgAuxArrInts => _lgAuxArrInts;

    /// <summary>The raw table, including empty slots.</summary>
    public int[] AuxIntArr => _auxIntArr;

    public int CompactSizeBytes => _auxCount << 2;

    public int UpdatableSizeBytes => 4 << _lgAuxArrInts;

    public AuxHashMap Copy() => new(this);

    /// <summary>Adds an exception for a register that does not already have one.</summary>
    public void MustAdd(int slotNo, int value)
    {
        int index = Find(_auxIntArr, _lgAuxArrInts, _lgConfigK, slotNo);
        if (index >= 0)
        {
            throw new InvalidOperationException(
                $"Register {slotNo} already has an auxiliary entry; it should not.");
        }

        _auxIntArr[~index] = HllUtil.Pair(slotNo, value);
        _auxCount++;
        CheckGrow();
    }

    /// <summary>Returns the exception value for a register that must have one.</summary>
    public int MustFindValueFor(int slotNo)
    {
        int index = Find(_auxIntArr, _lgAuxArrInts, _lgConfigK, slotNo);
        if (index < 0)
        {
            throw new InvalidOperationException($"Register {slotNo} has no auxiliary entry, but should.");
        }
        return HllUtil.PairValue(_auxIntArr[index]);
    }

    /// <summary>Replaces the exception value for a register that must already have one.</summary>
    public void MustReplace(int slotNo, int value)
    {
        int index = Find(_auxIntArr, _lgAuxArrInts, _lgConfigK, slotNo);
        if (index < 0)
        {
            throw new InvalidOperationException($"Register {slotNo} has no auxiliary entry to replace.");
        }
        _auxIntArr[index] = HllUtil.Pair(slotNo, value);
    }

    /// <summary>Enumerates the stored pairs, skipping empty slots.</summary>
    public IEnumerable<int> Pairs()
    {
        foreach (int pair in _auxIntArr)
        {
            if (pair != HllUtil.Empty)
            {
                yield return pair;
            }
        }
    }

    /// <summary>
    /// Rebuilds this map from a serialized image.
    /// </summary>
    /// <param name="image">The whole sketch image.</param>
    /// <param name="offset">Byte offset where the auxiliary data begins.</param>
    /// <param name="lgConfigK">The sketch's lgK.</param>
    /// <param name="auxCount">Number of exceptions the preamble declares.</param>
    /// <param name="compact">Whether the image stores only the occupied entries.</param>
    public static AuxHashMap Deserialize(
        ReadOnlySpan<byte> image, int offset, int lgConfigK, int auxCount, bool compact)
    {
        // Early versions did not record the table size, so for compact images it
        // has to be inferred from the count.
        int lgAuxArrInts = compact
            ? HllPreamble.ComputeLgArr(image, auxCount, lgConfigK)
            : HllPreamble.ReadLgArr(image);

        var map = new AuxHashMap(lgAuxArrInts, lgConfigK);
        int configKmask = (1 << lgConfigK) - 1;

        if (compact)
        {
            for (int i = 0; i < auxCount; i++)
            {
                int pair = HllPreamble.ReadInt(image, offset + (i << 2));
                map.MustAdd(HllUtil.PairLow26(pair) & configKmask, HllUtil.PairValue(pair));
            }
        }
        else
        {
            int auxArrInts = 1 << lgAuxArrInts;
            for (int i = 0; i < auxArrInts; i++)
            {
                int pair = HllPreamble.ReadInt(image, offset + (i << 2));
                if (pair == HllUtil.Empty) { continue; }
                map.MustAdd(HllUtil.PairLow26(pair) & configKmask, HllUtil.PairValue(pair));
            }
        }

        return map;
    }

    private void CheckGrow()
    {
        if (HllUtil.ResizeDenom * _auxCount > HllUtil.ResizeNumer * _auxIntArr.Length)
        {
            GrowAuxSpace();
        }
    }

    private void GrowAuxSpace()
    {
        int[] oldArray = _auxIntArr;
        int configKmask = (1 << _lgConfigK) - 1;
        _auxIntArr = new int[1 << ++_lgAuxArrInts];

        foreach (int fetched in oldArray)
        {
            if (fetched == HllUtil.Empty) { continue; }
            int index = Find(_auxIntArr, _lgAuxArrInts, _lgConfigK, fetched & configKmask);
            _auxIntArr[~index] = fetched;
        }
    }

    /// <summary>
    /// Open-addressing probe keyed on the register number alone, not the whole
    /// pair — a register has at most one exception, and the value changes.
    /// </summary>
    /// <returns>
    /// The index of the matching entry, or the bitwise complement of the index of
    /// the first empty slot if there is no match.
    /// </returns>
    private static int Find(int[] auxArr, int lgAuxArrInts, int lgConfigK, int slotNo)
    {
        int auxArrMask = (1 << lgAuxArrInts) - 1;
        int configKmask = (1 << lgConfigK) - 1;
        int probe = slotNo & auxArrMask;
        int loopIndex = probe;

        do
        {
            int arrVal = auxArr[probe];
            if (arrVal == HllUtil.Empty)
            {
                return ~probe;
            }
            if (slotNo == (arrVal & configKmask))
            {
                return probe;
            }
            int stride = ((int)((uint)slotNo >> lgAuxArrInts)) | 1;
            probe = (probe + stride) & auxArrMask;
        }
        while (probe != loopIndex);

        throw new InvalidOperationException("Auxiliary table is full and the register was not found.");
    }
}
