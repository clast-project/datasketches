// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Bit-compatible port of org.apache.datasketches.hash.MurmurHash3 from
// apache/datasketches-java, which is itself a port of Austin Appleby's
// public-domain MurmurHash3_x64_128 (revision 150). See NOTICE.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Clast.Sketches;

/// <summary>
/// MurmurHash3 x64 128-bit — the hash function every Apache DataSketches
/// sketch uses to map input values into hash space.
/// </summary>
/// <remarks>
/// <para>
/// Output is bit-identical to <c>org.apache.datasketches.hash.MurmurHash3</c>
/// and to Appleby's reference C++ <c>MurmurHash3_x64_128</c> for the same input
/// bytes and seed. That equality is what makes serialized sketches portable
/// across the Java, C++, Python, and this implementation — a sketch is only
/// mergeable with another if both were built with the same hash and seed.
/// </para>
/// <para>
/// The <c>long</c>-oriented overloads exist because sketches hash numeric keys
/// far more often than byte strings. They agree with the byte overload: hashing
/// a <see cref="long"/> gives the same result as hashing its 8 little-endian
/// bytes, so a numeric key hashes identically no matter which overload the
/// caller reaches for.
/// </para>
/// </remarks>
public static class MurmurHash3
{
    private const ulong C1 = 0x87c37b91114253d5UL;
    private const ulong C2 = 0x4cf5ad432745937fUL;

    /// <summary>
    /// Hashes the little-endian bytes of a single <see cref="long"/>. Equivalent
    /// to hashing an 8-byte span holding the same value.
    /// </summary>
    public static Hash128 Hash(long key, ulong seed)
    {
        ulong h1 = seed, h2 = seed;
        return FinalMix128(ref h1, ref h2, (ulong)key, 0UL, sizeof(long));
    }

    /// <summary>
    /// Hashes a span of bytes.
    /// </summary>
    /// <remarks>
    /// An empty span hashes to the well-defined MurmurHash3 of zero-length input
    /// rather than throwing. The Java reference rejects empty input here, but it
    /// does so as a guard for the sketch layer, which rejects null and empty
    /// values before it ever hashes them; this port enforces that at the same
    /// place. Non-empty input is bit-identical either way.
    /// </remarks>
    public static Hash128 Hash(ReadOnlySpan<byte> key, ulong seed)
    {
        ulong h1 = seed, h2 = seed;
        int lengthBytes = key.Length;

        // Body: full 128-bit blocks of 16 bytes.
        int nblocks = lengthBytes >> 4;
        for (int i = 0; i < nblocks; i++)
        {
            ulong k1 = ReadUInt64(key, i << 4);
            ulong k2 = ReadUInt64(key, (i << 4) + 8);
            BlockMix128(ref h1, ref h2, k1, k2);
        }

        // Tail: 0..15 remaining bytes, assembled little-endian.
        int tail = nblocks << 4;
        int rem = lengthBytes - tail;
        ulong t1, t2;
        if (rem > 8)
        {
            t1 = ReadUInt64(key, tail);
            t2 = ReadPartialUInt64(key, tail + 8, rem - 8);
        }
        else
        {
            t1 = rem == 0 ? 0UL : ReadPartialUInt64(key, tail, rem);
            t2 = 0UL;
        }

        return FinalMix128(ref h1, ref h2, t1, t2, (ulong)lengthBytes);
    }

    /// <summary>
    /// Hashes a span of <see cref="long"/> values, treating each as 8
    /// little-endian bytes. Matches <see cref="Hash(ReadOnlySpan{byte}, ulong)"/>
    /// over the equivalent byte sequence.
    /// </summary>
    public static Hash128 Hash(ReadOnlySpan<long> key, ulong seed)
    {
        ulong h1 = seed, h2 = seed;
        int lengthLongs = key.Length;

        // Body: full 128-bit blocks of 2 longs.
        int nblocks = lengthLongs >> 1;
        for (int i = 0; i < nblocks; i++)
        {
            BlockMix128(ref h1, ref h2, (ulong)key[i << 1], (ulong)key[(i << 1) + 1]);
        }

        // Tail: at most one whole long left over, so k2 is always zero.
        int tail = nblocks << 1;
        ulong t1 = lengthLongs - tail == 0 ? 0UL : (ulong)key[tail];

        return FinalMix128(ref h1, ref h2, t1, 0UL, (ulong)lengthLongs << 3);
    }

    /// <summary>Mixes one full 128-bit block of input into the hash state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlockMix128(ref ulong h1, ref ulong h2, ulong k1, ulong k2)
    {
        h1 ^= MixK1(k1);
        h1 = RotateLeft(h1, 27);
        h1 += h2;
        h1 = (h1 * 5) + 0x52dce729UL;

        h2 ^= MixK2(k2);
        h2 = RotateLeft(h2, 31);
        h2 += h1;
        h2 = (h2 * 5) + 0x38495ab5UL;
    }

    /// <summary>Mixes the tail and the input length in, then finalizes both halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Hash128 FinalMix128(ref ulong h1, ref ulong h2, ulong k1, ulong k2, ulong inputLengthBytes)
    {
        h1 ^= MixK1(k1);
        h2 ^= MixK2(k2);
        h1 ^= inputLengthBytes;
        h2 ^= inputLengthBytes;
        h1 += h2;
        h2 += h1;
        h1 = FinalMix64(h1);
        h2 = FinalMix64(h2);
        h1 += h2;
        h2 += h1;
        return new Hash128(h1, h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FinalMix64(ulong h)
    {
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MixK1(ulong k1)
    {
        k1 *= C1;
        k1 = RotateLeft(k1, 31);
        k1 *= C2;
        return k1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MixK2(ulong k2)
    {
        k2 *= C2;
        k2 = RotateLeft(k2, 33);
        k2 *= C1;
        return k2;
    }

    // Written as the shift-or idiom rather than BitOperations.RotateLeft so the
    // netstandard2.0 build compiles; RyuJIT folds this to a single `rol` anyway.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));

    /// <summary>Assembles 1..7 trailing bytes into the low bytes of a ulong, little-endian.</summary>
    private static ulong ReadPartialUInt64(ReadOnlySpan<byte> source, int offset, int count)
    {
        ulong result = 0UL;
        for (int i = count; i-- > 0;)
        {
            result |= (ulong)source[offset + i] << (i << 3);
        }
        return result;
    }
}
