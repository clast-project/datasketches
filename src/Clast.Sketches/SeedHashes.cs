// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// Computes the 16-bit seed hash that Theta-family sketches carry in their
/// preamble.
/// </summary>
/// <remarks>
/// Sketches may only be merged if they were built with the same update seed,
/// because the hash-to-key mapping has to be identical on both sides. Storing
/// the full 64-bit seed in every image would be wasteful, so DataSketches
/// stores 16 bits of its hash and compares that — enough to catch the mistake
/// in practice, and cheap.
/// </remarks>
internal static class SeedHashes
{
    /// <summary>
    /// Computes the seed hash of <paramref name="seed"/>: the low 16 bits of
    /// the MurmurHash3 of the seed, hashed with a seed of zero.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The seed hashes to zero. Zero is reserved as the "absent" value written
    /// into empty sketch images, so such a seed could not be validated.
    /// </exception>
    public static ushort Compute(ulong seed)
    {
        ushort seedHash = (ushort)(MurmurHash3.Hash((long)seed, 0UL).H1 & 0xFFFFUL);
        if (seedHash == 0)
        {
            throw new ArgumentException(
                $"The seed {seed} produces a seed hash of zero, which is reserved. Choose a different seed.",
                nameof(seed));
        }
        return seedHash;
    }

    /// <summary>Throws if the seed hash found in an image does not match the expected one.</summary>
    public static void Check(ushort actual, ushort expected)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Incompatible seed hashes: image has 0x{actual:x4}, expected 0x{expected:x4}. " +
                "The sketch was built with a different update seed.");
        }
    }
}
