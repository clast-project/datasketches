// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// Bit-counting helpers.
/// </summary>
/// <remarks>
/// Hand-rolled because <c>System.Numerics.BitOperations</c> does not exist on
/// netstandard2.0. These feed directly into serialized formats — a leading-zero
/// count off by one changes an HLL register value or a Theta entry width — so
/// they match Java's <c>numberOfLeadingZeros</c> exactly, including returning
/// the full width for zero.
/// </remarks>
internal static class Bits
{
    /// <summary>Counts leading zero bits of a 64-bit value; 64 if it is zero.</summary>
    public static int LeadingZeroCount(ulong value)
    {
        if (value == 0) { return 64; }

        int n = 0;
        if ((value >> 32) == 0) { n += 32; value <<= 32; }
        if ((value >> 48) == 0) { n += 16; value <<= 16; }
        if ((value >> 56) == 0) { n += 8; value <<= 8; }
        if ((value >> 60) == 0) { n += 4; value <<= 4; }
        if ((value >> 62) == 0) { n += 2; value <<= 2; }
        if ((value >> 63) == 0) { n += 1; }
        return n;
    }

    /// <summary>Counts leading zero bits of a 32-bit value; 32 if it is zero.</summary>
    public static int LeadingZeroCount(uint value) => LeadingZeroCount((ulong)value) - 32;

    /// <summary>The number of bits needed to represent <paramref name="value"/>; 0 if it is zero.</summary>
    public static int BitLength(int value) => 32 - LeadingZeroCount((uint)value);

    /// <summary>Whole bytes needed to hold <paramref name="bits"/> bits.</summary>
    public static int WholeBytesToHoldBits(int bits) => (bits >> 3) + ((bits & 7) > 0 ? 1 : 0);
}
