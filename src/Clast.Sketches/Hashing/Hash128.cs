// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// A 128-bit hash value, as two 64-bit halves. The equivalent of the
/// <c>long[2]</c> returned by the Apache DataSketches Java
/// <c>MurmurHash3</c>: <see cref="H1"/> is element 0 and <see cref="H2"/>
/// is element 1.
/// </summary>
/// <remarks>
/// Sketches use the halves differently — Theta keys off the low 63 bits of
/// <see cref="H1"/>, HLL uses <see cref="H1"/> to pick a slot and
/// <see cref="H2"/> to count leading zeros — so both halves are exposed
/// rather than folded together.
/// </remarks>
public readonly struct Hash128 : IEquatable<Hash128>
{
    /// <summary>Creates a 128-bit hash from its two halves.</summary>
    public Hash128(ulong h1, ulong h2)
    {
        H1 = h1;
        H2 = h2;
    }

    /// <summary>The first 64-bit half (element 0 of the Java <c>long[2]</c>).</summary>
    public ulong H1 { get; }

    /// <summary>The second 64-bit half (element 1 of the Java <c>long[2]</c>).</summary>
    public ulong H2 { get; }

    /// <inheritdoc/>
    public bool Equals(Hash128 other) => H1 == other.H1 && H2 == other.H2;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Hash128 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        ulong mixed = H1 ^ H2;
        return (int)mixed ^ (int)(mixed >> 32);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{H1:x16}{H2:x16}";

    /// <summary>Tests two hashes for equality.</summary>
    public static bool operator ==(Hash128 left, Hash128 right) => left.Equals(right);

    /// <summary>Tests two hashes for inequality.</summary>
    public static bool operator !=(Hash128 left, Hash128 right) => !left.Equals(right);
}
