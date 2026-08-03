// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Port of org.apache.datasketches.theta.BitPacking from apache/datasketches-java.
// See NOTICE.

using System.Buffers.Binary;

namespace Clast.Sketches.Theta;

/// <summary>
/// Reads and writes a stream of fixed-width values packed without padding,
/// most-significant bit first.
/// </summary>
/// <remarks>
/// <para>
/// This backs the delta-compressed Theta form. An ordered compact sketch's
/// hashes are close together relative to their magnitude, so the gaps between
/// consecutive hashes need far fewer than 64 bits — typically 20 to 50 — and
/// storing gaps at their true width is most of the compression.
/// </para>
/// <para>
/// The reference implementation hand-unrolls this into 63 specialized routines,
/// one per width, so that every shift is a compile-time constant and no value
/// is ever assembled a byte at a time. The same end is reached here differently:
/// because the stream is most-significant-bit first, the eight bytes starting at
/// a value's own byte form a big-endian 64-bit window containing it, so each
/// value costs one load and a pair of shifts regardless of width. It emits
/// exactly the same bit stream, which the TCK snapshots confirm byte for byte.
/// </para>
/// </remarks>
internal static class ThetaBitPacking
{
    /// <summary>
    /// Widest entry the fast paths accept. A value begins at most 7 bits into a
    /// byte, so both the reader's 64-bit window and the writer's accumulator can
    /// hold one whole value plus that leading offset only up to 56 bits. Wider
    /// entries fall back to the general routines — which happens only for
    /// sketches small enough that it cannot matter, since a wide entry means
    /// large gaps and therefore few of them.
    /// </summary>
    private const int MaxWindowBits = 56;

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
    /// Packs the gaps between consecutive ascending hashes, computing them on the
    /// way rather than from a prepared array.
    /// </summary>
    /// <remarks>
    /// Fused because the caller has no other use for the gaps: materializing them
    /// first would cost an array as large as the sketch and a second pass over
    /// it.
    /// </remarks>
    /// <returns>The number of bytes written.</returns>
    public static int PackDeltas(ReadOnlySpan<long> ascending, int bits, byte[] buffer, int bufOffset)
    {
        if (bits > MaxWindowBits)
        {
            return PackDeltasGeneral(ascending, bits, buffer, bufOffset);
        }

        // Writing uses an accumulator rather than the 64-bit window the reader
        // uses. Consecutive values share bytes, so a windowed writer would load
        // a word it had just stored to, and each overlapping load stalls on
        // store-to-load forwarding. Measured 1.5x slower that way. Shifting
        // bytes out of an accumulator writes each byte exactly once instead.
        ulong accumulator = 0;
        int accumulatedBits = 0;
        int at = bufOffset;
        long previous = 0;

        foreach (long hash in ascending)
        {
            // Values are known to fit in `bits`: the entry width was chosen as
            // the widest gap present, so no masking is needed.
            accumulator |= (ulong)(hash - previous) << (64 - accumulatedBits - bits);
            previous = hash;
            accumulatedBits += bits;

            while (accumulatedBits >= 8)
            {
                buffer[at++] = (byte)(accumulator >> 56);
                accumulator <<= 8;
                accumulatedBits -= 8;
            }
        }

        if (accumulatedBits > 0)
        {
            buffer[at++] = (byte)(accumulator >> 56);
        }

        return at - bufOffset;
    }

    /// <summary>
    /// How many leading values can be addressed through a full 64-bit window
    /// without reading or writing past the end of the buffer.
    /// </summary>
    private static int WindowedCount(int bufferLength, int bufOffset, int bits, int count)
    {
        int slack = bufferLength - bufOffset - 8;
        if (slack < 0)
        {
            return 0;
        }

        // The window for value i starts at byte (i * bits) / 8, which must be at
        // most `slack` for its eight bytes to be in bounds.
        long maxIndex = (((long)slack * 8) + 7) / bits;
        return (int)Math.Min(count, maxIndex + 1);
    }

    /// <summary>Unpacks <paramref name="count"/> values of <paramref name="bits"/> bits each.</summary>
    public static void Unpack(Span<long> values, int bits, ReadOnlySpan<byte> buffer, int bufOffset, int count)
    {
        int windowed = bits <= MaxWindowBits ? WindowedCount(buffer.Length, bufOffset, bits, count) : 0;

        for (int i = 0; i < windowed; i++)
        {
            long bitPos = (long)i * bits;
            int at = bufOffset + (int)(bitPos >> 3);
            int bitOffset = (int)(bitPos & 7);

            // One big-endian load per value: shift the value up to the top of the
            // window, then down to the bottom. No per-byte work at all.
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(at));
            values[i] = (long)((word << bitOffset) >> (64 - bits));
        }

        for (int i = windowed; i < count; i++)
        {
            long bitPos = (long)i * bits;
            values[i] = UnpackBits(bits, buffer, bufOffset + (int)(bitPos >> 3), (int)(bitPos & 7));
        }
    }

    /// <summary>
    /// The general packer, one value at a time. Handles widths the accumulator
    /// cannot take, and anchors the parity tests.
    /// </summary>
    public static int PackGeneral(ReadOnlySpan<long> values, int bits, byte[] buffer, int bufOffset, int count)
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

    /// <summary>The general unpacker, one value at a time.</summary>
    public static void UnpackGeneral(
        Span<long> values, int bits, ReadOnlySpan<byte> buffer, int bufOffset, int count)
    {
        int bitOffset = 0;

        for (int i = 0; i < count; i++)
        {
            values[i] = UnpackBits(bits, buffer, bufOffset, bitOffset);
            bufOffset += (bitOffset + bits) >> 3;
            bitOffset = (bitOffset + bits) & 7;
        }
    }

    private static int PackDeltasGeneral(ReadOnlySpan<long> ascending, int bits, byte[] buffer, int bufOffset)
    {
        int start = bufOffset;
        int bitOffset = 0;
        long previous = 0;

        foreach (long hash in ascending)
        {
            PackBits(hash - previous, bits, buffer, bufOffset, bitOffset);
            previous = hash;
            bufOffset += (bitOffset + bits) >> 3;
            bitOffset = (bitOffset + bits) & 7;
        }

        return (bufOffset - start) + (bitOffset > 0 ? 1 : 0);
    }
}
