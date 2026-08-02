// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.hll.Hll4Update from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// The register update for <see cref="Hll4Array"/>, which is the only one of the
/// three that has to manage a running minimum and an overflow table.
/// </summary>
internal static class Hll4Update
{
    /// <summary>
    /// Raises a register to <paramref name="newValue"/> if that is larger than
    /// what it currently holds.
    /// </summary>
    public static void InternalUpdate(Hll4Array host, int slotNo, int newValue)
    {
        int curMin = host.CurMin;
        int rawStoredOldNibble = host.GetNibble(slotNo);

        // The nibble is relative to curMin, so this is a guaranteed lower bound on
        // the register's real value — and if the new value does not beat even that,
        // there is nothing to do and no need to resolve the auxiliary table.
        int lbOnOldValue = rawStoredOldNibble + curMin;
        if (newValue <= lbOnOldValue)
        {
            return;
        }

        int actualOldValue;

        if (rawStoredOldNibble == HllUtil.AuxToken)
        {
            // The register is already an exception, so its real value lives in the
            // auxiliary table and may still exceed the new one.
            AuxHashMap auxHashMap = host.AuxHashMap!;
            actualOldValue = auxHashMap.MustFindValueFor(slotNo);
            if (newValue <= actualOldValue)
            {
                return;
            }

            HllArray.HipAndKxQIncrementalUpdate(host, actualOldValue, newValue);

            // It was an exception and curMin has not moved, so it still is one.
            auxHashMap.MustReplace(slotNo, newValue);
        }
        else
        {
            actualOldValue = lbOnOldValue;
            HllArray.HipAndKxQIncrementalUpdate(host, actualOldValue, newValue);

            int shiftedNewValue = newValue - curMin;
            if (shiftedNewValue >= HllUtil.AuxToken)
            {
                // Newly an exception: mark the nibble and record the real value.
                host.PutNibble(slotNo, HllUtil.AuxToken);
                host.AuxHashMap ??= host.NewAuxHashMap();
                host.AuxHashMap.MustAdd(slotNo, newValue);
            }
            else
            {
                host.PutNibble(slotNo, shiftedNewValue);
            }
        }

        // Raising a register off the current minimum may have been the last one
        // there, in which case the whole array can shift down.
        if (actualOldValue == curMin)
        {
            host.NumAtCurMin--;
            while (host.NumAtCurMin == 0)
            {
                ShiftToBiggerCurMin(host);
            }
        }
    }

    /// <summary>
    /// Raises the minimum by one and re-bases every register against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each nibble drops by one, which frees a value at the top of the range —
    /// so a register that was an exception at 15 may now fit inline at 14 and
    /// leaves the auxiliary table. The table is therefore rebuilt rather than
    /// edited.
    /// </para>
    /// <para>
    /// HIP and the KxQ registers are untouched: this changes how values are
    /// encoded, not what they are.
    /// </para>
    /// </remarks>
    private static void ShiftToBiggerCurMin(Hll4Array host)
    {
        int oldCurMin = host.CurMin;
        int newCurMin = oldCurMin + 1;
        int configK = 1 << host.LgConfigK;
        int configKmask = configK - 1;

        int numAtNewCurMin = 0;
        int numAuxTokens = 0;

        for (int i = 0; i < configK; i++)
        {
            int oldStoredNibble = host.GetNibble(i);
            if (oldStoredNibble == 0)
            {
                throw new InvalidOperationException(
                    "Register nibble is zero while raising the minimum; the sketch is corrupt.");
            }

            if (oldStoredNibble < HllUtil.AuxToken)
            {
                host.PutNibble(i, --oldStoredNibble);
                if (oldStoredNibble == 0)
                {
                    numAtNewCurMin++;
                }
            }
            else
            {
                numAuxTokens++;
            }
        }

        AuxHashMap? newAuxMap = null;
        AuxHashMap? oldAuxMap = host.AuxHashMap;

        if (oldAuxMap is not null)
        {
            foreach (int pair in oldAuxMap.Pairs())
            {
                int slotNum = HllUtil.PairLow26(pair) & configKmask;
                int oldActualVal = HllUtil.PairValue(pair);
                int newShiftedVal = oldActualVal - newCurMin;

                if (newShiftedVal < HllUtil.AuxToken)
                {
                    // No longer an exception; fold it back into the nibble array.
                    host.PutNibble(slotNum, newShiftedVal);
                    numAuxTokens--;
                }
                else
                {
                    newAuxMap ??= new AuxHashMap(HllUtil.LgAuxArrInts[host.LgConfigK], host.LgConfigK);
                    newAuxMap.MustAdd(slotNum, oldActualVal);
                }
            }
        }
        else if (numAuxTokens != 0)
        {
            throw new InvalidOperationException(
                $"Found {numAuxTokens} exception registers with no auxiliary table.");
        }

        if (newAuxMap is not null && newAuxMap.AuxCount != numAuxTokens)
        {
            throw new InvalidOperationException(
                $"Auxiliary table holds {newAuxMap.AuxCount} entries but the register array marks {numAuxTokens}.");
        }

        host.AuxHashMap = newAuxMap;
        host.CurMin = newCurMin;
        host.NumAtCurMin = numAtNewCurMin;
    }
}
