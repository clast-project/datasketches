// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// How many bits an HLL sketch spends on each of its <c>k</c> registers.
/// </summary>
/// <remarks>
/// <para>
/// The three are isomorphic: given the same <c>lgK</c> and the same input they
/// produce identical estimates with identical error. They differ only in the
/// space/speed trade-off of the final HLL array, and the choice is recorded in
/// the serialized image so a reader gets back what was written.
/// </para>
/// <list type="bullet">
/// <item><see cref="Hll8"/> — one byte per register. Fastest to update, about <c>k</c> bytes.</item>
/// <item><see cref="Hll6"/> — six bits packed. About <c>3k/4</c> bytes.</item>
/// <item><see cref="Hll4"/> — four bits packed, with a small side table for the rare
/// registers that do not fit. Smallest at about <c>k/2</c> bytes, slowest to update.</item>
/// </list>
/// <para>
/// The numeric values are the type ordinals stored in the preamble and must not
/// be changed.
/// </para>
/// </remarks>
public enum TgtHllType
{
    /// <summary>Four bits per register, with an auxiliary table for exceptions.</summary>
    Hll4 = 0,

    /// <summary>Six bits per register.</summary>
    Hll6 = 1,

    /// <summary>Eight bits per register.</summary>
    Hll8 = 2,
}

/// <summary>The stage of an HLL sketch's life, which determines its layout.</summary>
/// <remarks>
/// A sketch starts as a plain list of coupons, grows into a hash set of them, and
/// only allocates the HLL register array once it has seen enough distinct values
/// to be worth it. That keeps a nearly-empty sketch to a handful of bytes. The
/// ordinals are stored in the preamble and must not be changed.
/// </remarks>
internal enum HllCurMode
{
    List = 0,
    Set = 1,
    Hll = 2,
}
