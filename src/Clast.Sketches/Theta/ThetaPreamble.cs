// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Clast.Sketches.Theta;

/// <summary>
/// Byte-level layout of the Theta sketch preamble. All knowledge of field
/// offsets and flag bits lives here so the sketch classes never index raw
/// bytes themselves.
/// </summary>
/// <remarks>
/// <para>
/// Multi-byte fields are little-endian on the wire. The Java reference writes
/// them in platform-native order and every platform DataSketches supports is
/// little-endian in practice, so little-endian is the format. Reading through
/// <see cref="BinaryPrimitives"/> keeps this correct on a big-endian host
/// rather than silently producing garbage.
/// </para>
/// <para>
/// The compact layouts, by preamble length:
/// </para>
/// <code>
/// preLongs = 1, empty:       [preLongs|serVer|famId|lgNom|lgArr|flags|seedHash]
/// preLongs = 1, single item: ... + [hash]
/// preLongs = 2, exact:       ... + [curCount|p] + [hash...]
/// preLongs = 3, estimating:  ... + [curCount|p] + [theta] + [hash...]
/// </code>
/// </remarks>
internal static class ThetaPreamble
{
    // Field offsets, in bytes from the start of the image.
    public const int PreambleLongsByte = 0;   // low 6 bits; high 2 bits are lgResizeFactor
    public const int LgResizeFactorBit = 6;   // position of the resize-factor bits within byte 0
    public const int SerVerByte = 1;
    public const int FamilyByte = 2;
    public const int LgNomLongsByte = 3;      // unused by compact images
    public const int LgArrLongsByte = 4;      // unused by compact images
    public const int FlagsByte = 5;
    public const int SeedHashShort = 6;
    public const int RetainedEntriesInt = 8;
    public const int PFloat = 12;             // unused by compact images; written as 0.0
    public const int ThetaLong = 16;

    // Flags byte bit masks.
    public const int ReservedFlagMask = 1;    // bit 0: was BigEndian, no longer used
    public const int ReadOnlyFlagMask = 2;    // bit 1
    public const int EmptyFlagMask = 4;       // bit 2
    public const int CompactFlagMask = 8;     // bit 3
    public const int OrderedFlagMask = 16;    // bit 4
    public const int SingleItemFlagMask = 32; // bit 5

    /// <summary>Serialization version of the standard (uncompressed) compact form.</summary>
    public const int SerVer = 3;

    /// <summary>Serialization version of the delta-compressed compact form.</summary>
    public const int SerVerCompressed = 4;

    /// <summary>
    /// The canonical 8-byte image of an empty compact sketch. Note the seed hash
    /// is zero: an empty sketch carries no hashes, so there is nothing to be
    /// incompatible about and readers skip the seed check.
    /// </summary>
    public static ReadOnlySpan<byte> EmptyImage => [1, 3, 3, 0, 0, 0x1E, 0, 0];

    public static int ReadPreambleLongs(ReadOnlySpan<byte> image) => image[PreambleLongsByte] & 0x3F;

    public static int ReadSerVer(ReadOnlySpan<byte> image) => image[SerVerByte];

    public static int ReadFamilyId(ReadOnlySpan<byte> image) => image[FamilyByte];

    public static int ReadFlags(ReadOnlySpan<byte> image) => image[FlagsByte];

    public static int ReadLgResizeFactor(ReadOnlySpan<byte> image) =>
        (image[PreambleLongsByte] >> LgResizeFactorBit) & 0x3;

    public static int ReadLgNomLongs(ReadOnlySpan<byte> image) => image[LgNomLongsByte];

    public static int ReadLgArrLongs(ReadOnlySpan<byte> image) => image[LgArrLongsByte];

    public static float ReadP(ReadOnlySpan<byte> image)
    {
        int bits = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(PFloat));
        return Unsafe.As<int, float>(ref bits);
    }

    public static ushort ReadSeedHash(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(SeedHashShort));

    public static int ReadRetainedEntries(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadInt32LittleEndian(image.Slice(RetainedEntriesInt));

    public static long ReadThetaLong(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadInt64LittleEndian(image.Slice(ThetaLong));

    /// <summary>
    /// Writes the first 8 bytes of a compact image. lgNomLongs, lgArrLongs and
    /// the resize factor are meaningless for compact sketches, but must be
    /// written rather than left undefined — the Java reference zeroes them too.
    /// </summary>
    public static void WriteHeader(Span<byte> image, int preambleLongs, int familyId, int flags, ushort seedHash) =>
        WriteHeader(image, preambleLongs, lgResizeFactor: 0, familyId, lgNomLongs: 0, lgArrLongs: 0, flags, seedHash);

    /// <summary>Writes the first 8 bytes of an image, including the update-sketch-only fields.</summary>
    public static void WriteHeader(
        Span<byte> image,
        int preambleLongs,
        int lgResizeFactor,
        int familyId,
        int lgNomLongs,
        int lgArrLongs,
        int flags,
        ushort seedHash)
    {
        image[PreambleLongsByte] = (byte)((preambleLongs & 0x3F) | ((lgResizeFactor & 0x3) << LgResizeFactorBit));
        image[SerVerByte] = SerVer;
        image[FamilyByte] = (byte)familyId;
        image[LgNomLongsByte] = (byte)lgNomLongs;
        image[LgArrLongsByte] = (byte)lgArrLongs;
        image[FlagsByte] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(image.Slice(SeedHashShort), seedHash);
    }

    public static void WriteRetainedEntries(Span<byte> image, int count) =>
        BinaryPrimitives.WriteInt32LittleEndian(image.Slice(RetainedEntriesInt), count);

    /// <summary>
    /// Writes the sampling probability field. Compact images always store 0.0
    /// here — <c>p</c> only means something for an update sketch, and the C++
    /// implementation writes zero, so matching it keeps images byte-identical
    /// across languages.
    /// </summary>
    public static void WriteP(Span<byte> image, float p) =>
        // BitConverter.SingleToInt32Bits does not exist on netstandard2.0; a
        // reinterpret is equivalent and available everywhere.
        BinaryPrimitives.WriteInt32LittleEndian(image.Slice(PFloat), Unsafe.As<float, int>(ref p));

    public static void WriteThetaLong(Span<byte> image, long thetaLong) =>
        BinaryPrimitives.WriteInt64LittleEndian(image.Slice(ThetaLong), thetaLong);

    /// <summary>
    /// Detects the single-item compact form.
    /// </summary>
    /// <remarks>
    /// The single-item flag cannot be relied on: sketches written before it
    /// existed leave it clear. The reference implementation therefore matches on
    /// the rest of the shape instead — one preamble long, serVer at least 3, the
    /// Compact family, and flags of exactly ordered+compact+read-only with empty
    /// clear. Combined with a matching seed hash that is effectively conclusive.
    /// </remarks>
    public static bool IsSingleItem(int preambleLongs, int serVer, int familyId, int flags) =>
        preambleLongs == 1
        && serVer >= SerVer
        && familyId == (int)SketchFamily.Compact
        && (flags & 0x1F) == (ReadOnlyFlagMask | CompactFlagMask | OrderedFlagMask);

    /// <summary>
    /// Returns the preamble length a compact image needs. Estimating sketches
    /// need room for theta; exact multi-entry sketches need a count; empty and
    /// single-item sketches need neither.
    /// </summary>
    public static int ComputeCompactPreambleLongs(bool empty, int retainedEntries, long thetaLong) =>
        thetaLong < long.MaxValue ? 3
        : empty ? 1
        : retainedEntries > 1 ? 2
        : 1;
}
