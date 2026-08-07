// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// The four shapes a serialized KLL image can take.
/// </summary>
/// <remarks>
/// The shape is not a field: it is recovered from the preamble length and the
/// serialization version together, which is why the two are validated as a
/// pair. Only <see cref="CompactEmpty"/>, <see cref="CompactSingle"/> and
/// <see cref="CompactFull"/> are ever written by <c>ToByteArray</c>;
/// <see cref="Updatable"/> is read-only support for images produced by the
/// reference library's off-heap sketches.
/// </remarks>
internal enum KllStructure
{
    /// <summary>8-byte preamble, no data. <c>preInts = 2, serVer = 1</c>.</summary>
    CompactEmpty,

    /// <summary>8-byte preamble followed by one item. <c>preInts = 2, serVer = 2</c>.</summary>
    CompactSingle,

    /// <summary>20-byte preamble, shortened levels array, retained items only. <c>preInts = 5, serVer = 1</c>.</summary>
    CompactFull,

    /// <summary>20-byte preamble, full levels array, items including free space. <c>preInts = 5, serVer = 3</c>.</summary>
    Updatable,
}

/// <summary>
/// Byte-level layout of the KLL preamble.
/// </summary>
/// <remarks>
/// <para>
/// Multi-byte fields are little-endian on the wire, for the same reason as in
/// the Theta and HLL formats: the reference writes native order and every
/// platform that matters is little-endian.
/// </para>
/// <code>
/// Compact empty / compact single (8 bytes + optional item):
///   0: preInts | 1: serVer | 2: familyId | 3: flags
///   4: -------- k -------- | 6: m        | 7: unused
///   8: {single item}
///
/// Compact full / updatable (20-byte preamble):
///   0: preInts | 1: serVer | 2: familyId | 3: flags
///   4: -------- k -------- | 6: m        | 7: unused
///   8: ------------------ n (8 bytes) ------------------
///  16: ------ minK ------- | 18: numLvls | 19: unused
///  20: {levels array}{min item}{max item}{items array}
/// </code>
/// </remarks>
internal static class KllPreamble
{
    // Field offsets, in bytes from the start of the image.
    public const int PreambleIntsByte = 0;
    public const int SerVerByte = 1;
    public const int FamilyByte = 2;
    public const int FlagsByte = 3;
    public const int KShort = 4;
    public const int MByte = 6;
    public const int NLong = 8;
    public const int MinKShort = 16;
    public const int NumLevelsByte = 18;

    /// <summary>Where data begins in a compact-empty or compact-single image.</summary>
    public const int DataStartSingleItem = 8;

    /// <summary>Where data begins in a compact-full or updatable image.</summary>
    public const int DataStart = 20;

    // Serialization versions.
    public const byte SerVerEmptyFull = 1;
    public const byte SerVerSingle = 2;
    public const byte SerVerUpdatable = 3;

    // Preamble lengths, in 32-bit words.
    public const byte PreambleIntsEmptySingle = 2;
    public const byte PreambleIntsFull = 5;

    // Flags byte bit masks.
    public const int EmptyFlagMask = 1;
    public const int LevelZeroSortedFlagMask = 2;
    public const int SingleItemFlagMask = 4;

    /// <summary>Preamble length in words for a structure.</summary>
    public static byte PreambleIntsFor(KllStructure structure) =>
        structure is KllStructure.CompactEmpty or KllStructure.CompactSingle
            ? PreambleIntsEmptySingle
            : PreambleIntsFull;

    /// <summary>Serialization version for a structure.</summary>
    public static byte SerVerFor(KllStructure structure) => structure switch
    {
        KllStructure.CompactSingle => SerVerSingle,
        KllStructure.Updatable => SerVerUpdatable,
        _ => SerVerEmptyFull,
    };

    /// <summary>
    /// Recovers the structure from the preamble length and serialization
    /// version. Combinations the format does not define are rejected here
    /// rather than producing a plausible-looking but wrong read.
    /// </summary>
    public static KllStructure StructureFrom(int preInts, int serVer) => (preInts, serVer) switch
    {
        (PreambleIntsEmptySingle, SerVerEmptyFull) => KllStructure.CompactEmpty,
        (PreambleIntsEmptySingle, SerVerSingle) => KllStructure.CompactSingle,
        (PreambleIntsFull, SerVerEmptyFull) => KllStructure.CompactFull,
        (PreambleIntsFull, SerVerUpdatable) => KllStructure.Updatable,
        _ => throw new ArgumentException(
            $"Not a valid KLL preamble: preambleInts {preInts} with serialization version {serVer}."),
    };
}
