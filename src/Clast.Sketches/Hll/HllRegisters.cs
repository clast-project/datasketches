// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Clast.Sketches.Hll;

/// <summary>
/// Bulk operations over an <see cref="TgtHllType.Hll8"/> register array.
/// </summary>
/// <remarks>
/// <para>
/// One byte per register makes merging a plain element-wise maximum, which is a
/// natural fit for SIMD and is the dominant cost in a union — every merge walks
/// every register.
/// </para>
/// <para>
/// AVX2 and ARM NEON take the wide paths and a scalar loop runs everywhere
/// else, including the netstandard2.0 build. All three produce identical
/// results: maximum is exact and order-independent, so unlike a floating-point
/// sum there is nothing here that vectorizing could perturb.
/// </para>
/// </remarks>
internal static class HllRegisters
{
    /// <summary>
    /// Raises each register of <paramref name="target"/> to the larger of itself
    /// and the corresponding register of <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Both arrays must be the same length, which is the case whenever two
    /// sketches share an <c>lgK</c> — the overwhelmingly common merge.
    /// </remarks>
    public static void MaxInto(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int i = 0;
        int length = source.Length;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && length >= Vector256<byte>.Count)
        {
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> merged = Vector256.Max(
                    Vector256.Create(source.Slice(i, Vector256<byte>.Count)),
                    Vector256.Create((ReadOnlySpan<byte>)target.Slice(i, Vector256<byte>.Count)));
                merged.CopyTo(target.Slice(i, Vector256<byte>.Count));
            }
        }
        else if (Vector128.IsHardwareAccelerated
            && (Sse2.IsSupported || AdvSimd.IsSupported)
            && length >= Vector128<byte>.Count)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> merged = Vector128.Max(
                    Vector128.Create(source.Slice(i, Vector128<byte>.Count)),
                    Vector128.Create((ReadOnlySpan<byte>)target.Slice(i, Vector128<byte>.Count)));
                merged.CopyTo(target.Slice(i, Vector128<byte>.Count));
            }
        }
#endif

        MaxIntoScalar(source.Slice(i), target.Slice(i));
    }

    /// <summary>
    /// The portable element-wise maximum. Handles the tail of the vector paths,
    /// runs whole on hardware without SIMD, and gives the parity tests something
    /// to compare the vector paths against.
    /// </summary>
    public static void MaxIntoScalar(ReadOnlySpan<byte> source, Span<byte> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] > target[i])
            {
                target[i] = source[i];
            }
        }
    }

    /// <summary>
    /// Folds a finer register array into a coarser one, taking the maximum over
    /// each group that maps to the same target register.
    /// </summary>
    /// <remarks>
    /// The target length is always a power-of-two fraction of the source's, so
    /// this is a sequence of whole passes of <see cref="MaxInto"/> rather than a
    /// scattered write.
    /// </remarks>
    public static void MaxIntoFolded(ReadOnlySpan<byte> source, Span<byte> target)
    {
        if (target.Length == 0 || source.Length % target.Length != 0)
        {
            throw new ArgumentException(
                $"Source length {source.Length} must be a whole multiple of target length {target.Length}.",
                nameof(source));
        }

        for (int offset = 0; offset < source.Length; offset += target.Length)
        {
            MaxInto(source.Slice(offset, target.Length), target);
        }
    }
}
