// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Sketches.Theta;

/// <summary>
/// The open-addressing, double-hashing table that Theta update sketches keep
/// their retained hashes in.
/// </summary>
/// <remarks>
/// <para>
/// The table is always a power of two long. The low <c>lgArrLongs</c> bits of a
/// hash give the starting probe; a second, odd stride derived from the next
/// seven bits keeps colliding keys from walking the same path. A slot holding
/// zero is empty — which is why a hash of zero can never be stored, and why
/// hashes are masked to 63 bits so they are always positive.
/// </para>
/// <para>
/// Probe order depends only on the hash and the table size, so a table rebuilt
/// from the same hashes lands in exactly the same layout. That is what lets a
/// serialized update sketch round-trip byte for byte.
/// </para>
/// </remarks>
internal static class ThetaHashTable
{
    private const int StrideHashBits = 7;

    /// <summary>Mask selecting the bits of a hash that determine its probe stride.</summary>
    public const int StrideMask = (1 << StrideHashBits) - 1;

    /// <summary>
    /// Computes the probe stride: odd (so it eventually visits every slot) and
    /// drawn from bits above those used for the initial index, so it is
    /// independent of the starting position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Stride(long hash, int lgArrLongs) =>
        (2 * (int)(((ulong)hash >> lgArrLongs) & StrideMask)) + 1;

    /// <summary>
    /// True if this hash should be skipped: it is zero (the empty marker),
    /// negative, or at or above theta.
    /// </summary>
    /// <remarks>
    /// The branch-free form is the reference implementation's: if either
    /// subtraction goes negative the OR of the two goes negative too.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContinueCondition(long thetaLong, long hash) =>
        ((hash - 1L) | (thetaLong - hash - 1L)) < 0L;

    public static void CheckHashCorruption(long hash)
    {
        // Hashes are the top bit of a 64-bit MurmurHash3 half shifted away, so a
        // negative value means the data did not come from this library.
        if (hash < 0L)
        {
            throw new InvalidDataException($"Data corruption: hash value is negative ({hash}).");
        }
    }

    public static void CheckThetaCorruption(long thetaLong)
    {
        if ((thetaLong | (thetaLong - 1)) < 0L)
        {
            throw new InvalidDataException($"Data corruption: theta must be positive, got {thetaLong}.");
        }
    }

    /// <summary>
    /// Inserts <paramref name="hash"/> unless it is already present.
    /// </summary>
    /// <returns>
    /// The index of the existing entry if this was a duplicate (always
    /// non-negative), or the bitwise complement of the index it was inserted at
    /// (always negative) if it was new.
    /// </returns>
    public static int SearchOrInsert(long[] table, int lgArrLongs, long hash)
    {
        int arrayMask = (1 << lgArrLongs) - 1;
        int stride = Stride(hash, lgArrLongs);
        int curProbe = (int)(hash & arrayMask);
        int loopIndex = curProbe;

        do
        {
            long value = table[curProbe];
            if (value == 0L)
            {
                table[curProbe] = hash;
                return ~curProbe;
            }
            if (value == hash)
            {
                return curProbe;
            }
            curProbe = (curProbe + stride) & arrayMask;
        }
        while (curProbe != loopIndex);

        throw new InvalidOperationException("Theta hash table is full and the value was not found.");
    }

    /// <summary>
    /// Rebuilds <paramref name="destination"/> from <paramref name="source"/>,
    /// dropping empty slots, duplicates, and anything at or above theta.
    /// </summary>
    /// <returns>The number of entries actually inserted.</returns>
    public static int ArrayInsert(long[] source, long[] destination, int lgArrLongs, long thetaLong)
    {
        CheckThetaCorruption(thetaLong);

        int count = 0;
        foreach (long hash in source)
        {
            CheckHashCorruption(hash);
            if (ContinueCondition(thetaLong, hash))
            {
                continue;
            }
            if (SearchOrInsert(destination, lgArrLongs, hash) < 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Counts the entries that are below theta — the sketch's valid retained count.</summary>
    public static int CountValid(long[] table, long thetaLong)
    {
        int count = 0;
        foreach (long hash in table)
        {
            if (!ContinueCondition(thetaLong, hash))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Extracts the valid entries of a hash table into a gap-free array — the
    /// compact form.
    /// </summary>
    /// <param name="table">The hash table. May contain empty slots and entries at or above theta.</param>
    /// <param name="retained">The expected number of valid entries.</param>
    /// <param name="thetaLong">Entries at or above this are dropped.</param>
    /// <param name="ordered">Whether to sort the result ascending.</param>
    public static long[] CompactCache(long[] table, int retained, long thetaLong, bool ordered)
    {
        if (retained == 0)
        {
            return [];
        }

        long[] output = new long[retained];
        int j = 0;
        foreach (long value in table)
        {
            if (value <= 0L || value >= thetaLong)
            {
                continue;
            }
            if (j == retained)
            {
                throw new InvalidOperationException(
                    $"Retained count {retained} is too low for the number of valid entries in the table.");
            }
            output[j++] = value;
        }

        if (j < retained)
        {
            throw new InvalidOperationException(
                $"Retained count {retained} is too high; the table holds only {j} valid entries.");
        }

        if (ordered && retained > 1)
        {
            Array.Sort(output);
        }
        return output;
    }
}
