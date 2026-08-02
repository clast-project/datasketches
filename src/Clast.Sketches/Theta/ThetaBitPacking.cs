// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.theta.BitPacking from apache/datasketches-java.
// See NOTICE.

namespace Clast.Sketches.Theta;

/// <summary>
/// Reads and writes a stream of fixed-width values packed without padding,
/// most-significant bit first.
/// </summary>
/// <remarks>
/// <para>
/// This backs the delta-compressed Theta form. An ordered compact sketch's
/// hashes are close together relative to their magnitude, so the gaps between
/// consecutive hashes need far fewer than 64 bits — typically 20 to 30 — and
/// storing gaps at their true width is most of the compression.
/// </para>
/// <para>
/// The reference implementation hand-unrolls this into 63 specialized routines,
/// one per width, for speed. They produce exactly this bit stream; the TCK
/// snapshots confirm it byte for byte.
/// </para>
/// </remarks>
internal static class ThetaBitPacking
{
    /// <summary>
    /// Writes the low <paramref name="bits"/> bits of <paramref name="value"/>
    /// at the given byte and bit offset.
    /// </summary>
    /// <remarks>
    /// Assumes the buffer is zeroed ahead of the write position: the first byte
    /// touched is assigned rather than OR-ed whenever the write starts on a byte
    /// boundary.
    /// </remarks>
    public static void PackBits(long value, int bits, byte[] buffer, int bufOffset, int bitOffset)
    {
        ulong v = (ulong)value;

        if (bitOffset > 0)
        {
            // Finish the partially filled byte first.
            int chunkBits = 8 - bitOffset;
            int mask = (1 << chunkBits) - 1;
            if (bits < chunkBits)
            {
                buffer[bufOffset] |= (byte)((v << (chunkBits - bits)) & (ulong)mask);
                return;
            }
            buffer[bufOffset++] |= (byte)((v >> (bits - chunkBits)) & (ulong)mask);
            bits -= chunkBits;
        }

        while (bits >= 8)
        {
            buffer[bufOffset++] = (byte)(v >> (bits - 8));
            bits -= 8;
        }

        if (bits > 0)
        {
            buffer[bufOffset] = (byte)(v << (8 - bits));
        }
    }

    /// <summary>Reads <paramref name="bits"/> bits from the given byte and bit offset.</summary>
    public static long UnpackBits(int bits, ReadOnlySpan<byte> buffer, int bufOffset, int bitOffset)
    {
        int availBits = 8 - bitOffset;
        int chunkBits = availBits <= bits ? availBits : bits;
        int mask = (1 << chunkBits) - 1;

        long value = (buffer[bufOffset] >> (availBits - chunkBits)) & mask;
        bufOffset += availBits == chunkBits ? 1 : 0;
        bits -= chunkBits;

        // The casts to uint are not redundant: a byte promotes to int, and OR-ing
        // an int into a long sign-extends it. The values here are never negative,
        // but the compiler cannot see that (CS0675), and an unsigned operand says
        // so rather than suppressing the warning.
        while (bits >= 8)
        {
            value <<= 8;
            value |= (uint)buffer[bufOffset++];
            bits -= 8;
        }

        if (bits > 0)
        {
            value <<= bits;
            value |= (uint)(buffer[bufOffset] >> (8 - bits));
        }

        return value;
    }

    /// <summary>
    /// Packs <paramref name="count"/> values of <paramref name="bits"/> bits each
    /// into a contiguous stream starting at <paramref name="bufOffset"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static int Pack(ReadOnlySpan<long> values, int bits, byte[] buffer, int bufOffset, int count)
    {
        int start = bufOffset;
        int bitOffset = 0;

        for (int i = 0; i < count; i++)
        {
            PackBits(values[i], bits, buffer, bufOffset, bitOffset);
            bufOffset += (bitOffset + bits) >> 3;
            bitOffset = (bitOffset + bits) & 7;
        }

        return (bufOffset - start) + (bitOffset > 0 ? 1 : 0);
    }

    /// <summary>Unpacks <paramref name="count"/> values of <paramref name="bits"/> bits each.</summary>
    public static void Unpack(Span<long> values, int bits, ReadOnlySpan<byte> buffer, int bufOffset, int count)
    {
        int bitOffset = 0;

        for (int i = 0; i < count; i++)
        {
            values[i] = UnpackBits(bits, buffer, bufOffset, bitOffset);
            bufOffset += (bitOffset + bits) >> 3;
            bitOffset = (bitOffset + bits) & 7;
        }
    }
}
