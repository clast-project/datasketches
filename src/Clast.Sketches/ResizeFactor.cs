// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// How aggressively an update sketch grows its internal hash table on the way
/// to full size.
/// </summary>
/// <remarks>
/// The enum values are the base-2 logarithm of the growth factor, which is
/// exactly what gets stored in the top two bits of preamble byte 0 — so the
/// numeric values must not be changed. A larger factor reaches full size in
/// fewer reallocations at the cost of overshooting more; a sketch that starts
/// small and stays small wastes less memory with a smaller factor.
/// </remarks>
public enum ResizeFactor
{
    /// <summary>Do not resize; allocate the sketch at full size immediately.</summary>
    X1 = 0,

    /// <summary>Double the table on each resize.</summary>
    X2 = 1,

    /// <summary>Quadruple the table on each resize.</summary>
    X4 = 2,

    /// <summary>Grow the table eightfold on each resize. The default.</summary>
    X8 = 3,
}

/// <summary>Helpers for <see cref="ResizeFactor"/>.</summary>
public static class ResizeFactorExtensions
{
    /// <summary>The base-2 logarithm of the growth factor, as stored in the preamble.</summary>
    public static int Lg(this ResizeFactor factor) => (int)factor;

    /// <summary>The growth factor itself: 1, 2, 4, or 8.</summary>
    public static int Value(this ResizeFactor factor) => 1 << (int)factor;

    /// <summary>
    /// Maps a stored log-factor back to a <see cref="ResizeFactor"/>. Values
    /// outside 0..3 cannot occur in a well-formed image; they fall back to
    /// <see cref="ResizeFactor.X8"/>, matching the reference implementation.
    /// </summary>
    public static ResizeFactor FromLg(int lg) =>
        lg is >= 0 and <= 3 ? (ResizeFactor)lg : ResizeFactor.X8;
}
