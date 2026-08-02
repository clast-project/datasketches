// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Sketches.Hll;

/// <summary>
/// Constants and bit-twiddling shared across the HLL implementation.
/// </summary>
/// <remarks>
/// A <em>coupon</em> is the 32-bit unit an HLL sketch actually stores before it
/// builds its register array: 26 bits of register address in the low half and a
/// 6-bit value — the number of leading zeros of a second hash half, plus one —
/// in the high half. Packing both into one int is what lets the early modes keep
/// a compact list rather than a full register array.
/// </remarks>
internal static class HllUtil
{
    /// <summary>Bits of a coupon holding the register address.</summary>
    public const int KeyBits26 = 26;

    /// <summary>Bits of a coupon holding the value.</summary>
    public const int ValBits6 = 6;

    public const int KeyMask26 = (1 << KeyBits26) - 1;
    public const int ValMask6 = (1 << ValBits6) - 1;

    /// <summary>An empty slot. A coupon is never zero, since its value is at least one.</summary>
    public const int Empty = 0;

    public const int MinLogK = 4;
    public const int MaxLogK = 21;

    /// <summary>Default <c>lgK</c>: 4096 registers, about 1.6% relative error.</summary>
    public const int DefaultLgK = 12;

    public const int LgInitListSize = 3;
    public const int LgInitSetSize = 5;

    /// <summary>Coupon tables grow when occupancy passes 3/4.</summary>
    public const int ResizeNumer = 3;
    public const int ResizeDenom = 4;

    public const int LoNibbleMask = 0x0F;
    public const int HiNibbleMask = 0xF0;

    /// <summary>
    /// The nibble value that means "this register's value lives in the auxiliary
    /// table". Only HLL_4 needs it: four bits cannot hold every value a register
    /// may reach.
    /// </summary>
    public const int AuxToken = 0xF;

    public static readonly double HllHipRseFactor = Math.Sqrt(Math.Log(2.0));

    public static readonly double HllNonHipRseFactor = Math.Sqrt((3.0 * Math.Log(2.0)) - 1.0);

    /// <summary>Relative error of the coupon-mode estimator at the transition point.</summary>
    public const double CouponRseFactor = 0.409;

    public const double CouponRse = CouponRseFactor / (1 << 13);

    /// <summary>Initial auxiliary-table size per <c>lgK</c>, as <c>lg(ints)</c>.</summary>
    public static readonly int[] LgAuxArrInts =
    [
        0, 2, 2, 2, 2, 2, 2, 3, 3, 3,
        4, 4, 5, 5, 6, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16, 17, 18,
    ];

    public static int CheckLgK(int lgK)
    {
        if (lgK is < MinLogK or > MaxLogK)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lgK), lgK, $"Log K must be between {MinLogK} and {MaxLogK}, inclusive.");
        }
        return lgK;
    }

    public static void CheckNumStdDev(int numStdDev)
    {
        if (numStdDev is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numStdDev), numStdDev, "Number of standard deviations must be 1, 2, or 3.");
        }
    }

    /// <summary>Packs a register address and value into a coupon.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pair(int slotNo, int value) => (value << KeyBits26) | (slotNo & KeyMask26);

    /// <summary>The register address held in a coupon.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PairLow26(int coupon) => coupon & KeyMask26;

    /// <summary>The register value held in a coupon.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PairValue(int coupon) => (int)((uint)coupon >> KeyBits26);

    /// <summary>
    /// Counts leading zero bits of a 64-bit value, matching Java's
    /// <c>Long.numberOfLeadingZeros</c>. The count feeds directly into the
    /// register value, so an off-by-one here changes every estimate.
    /// </summary>
    public static int NumberOfLeadingZeros(ulong value) => Bits.LeadingZeroCount(value);

    /// <summary>Base-2 logarithm of a value known to be an exact power of two.</summary>
    public static int ExactLog2(int powerOfTwo)
    {
        int lg = 0;
        while ((1 << lg) < powerOfTwo) { lg++; }
        return lg;
    }

    public static int CeilingPowerOf2(int value)
    {
        int result = 1;
        while (result < value) { result <<= 1; }
        return result;
    }

    /// <summary>2 raised to the negative of <paramref name="e"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double InvPow2(int e) =>
        // Builds the double's exponent field directly rather than calling Pow,
        // which the reference does too; this sits in the innermost update loop.
        BitConverter.Int64BitsToDouble((1023L - e) << 52);
}
