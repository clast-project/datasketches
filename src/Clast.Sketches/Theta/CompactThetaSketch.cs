// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Theta;

/// <summary>
/// An immutable Theta sketch in compact form: a gap-free array of retained
/// hashes plus theta and a seed hash. This is the form Theta sketches take when
/// they are serialized, and the form stored in Apache Iceberg Puffin
/// <c>apache-datasketches-theta-v1</c> blobs.
/// </summary>
/// <remarks>
/// The image is 8 bytes of preamble for an empty sketch, 16 for a single value,
/// and 16 or 24 plus 8 bytes per retained hash otherwise.
/// </remarks>
public sealed class CompactThetaSketch : ThetaSketch
{
    private readonly long[] _hashes;
    private readonly long _thetaLong;
    private readonly bool _empty;
    private readonly bool _ordered;
    private readonly ushort _seedHash;

    internal CompactThetaSketch(long[] hashes, long thetaLong, bool empty, bool ordered, ushort seedHash)
    {
        if (empty && hashes.Length != 0)
        {
            throw new InvalidDataException(
                $"Corrupt state: a sketch flagged empty cannot retain {hashes.Length} entries.");
        }

        // Retaining nothing at theta = 1.0 is indistinguishable from never having
        // seen a value, so normalize it to empty. Set operations can produce that
        // state — intersecting two disjoint exact sketches, for instance — and
        // leaving it un-normalized would serialize to an image readers reject.
        bool isEmpty = empty || (hashes.Length == 0 && thetaLong == long.MaxValue);

        _hashes = hashes;
        // An empty sketch is always theta = 1.0. An update sketch configured with
        // p < 1.0 but never updated would otherwise serialize as empty with
        // theta < 1.0, which readers treat as corrupt.
        _thetaLong = isEmpty ? long.MaxValue : thetaLong;
        _empty = isEmpty;
        // Zero or one entries are trivially in order; saying so lets a reader skip
        // a sort it does not need.
        _ordered = ordered || hashes.Length <= 1;
        _seedHash = seedHash;
    }

    /// <inheritdoc/>
    public override SketchFamily Family => SketchFamily.Compact;

    /// <inheritdoc/>
    public override bool IsEmpty => _empty;

    /// <inheritdoc/>
    public override bool IsOrdered => _ordered;

    /// <inheritdoc/>
    public override int RetainedEntries => _hashes.Length;

    /// <inheritdoc/>
    public override long ThetaLong => _thetaLong;

    /// <summary>
    /// The retained hash values, in the order they are stored. Each is a
    /// positive 63-bit value strictly below <see cref="ThetaSketch.ThetaLong"/>.
    /// </summary>
    public ReadOnlySpan<long> HashValues => _hashes;

    /// <inheritdoc/>
    public override ushort SeedHash => _seedHash;

    /// <inheritdoc/>
    internal override long[] HashCache => _hashes;

    /// <inheritdoc/>
    public override CompactThetaSketch Compact(bool ordered)
    {
        // Already compact. Only a request to order an unordered sketch does work,
        // and it copies rather than sorting in place — this type is immutable.
        if (!ordered || _ordered)
        {
            return this;
        }

        long[] sorted = (long[])_hashes.Clone();
        Array.Sort(sorted);
        return new CompactThetaSketch(sorted, _thetaLong, _empty, ordered: true, _seedHash);
    }

    /// <summary>The number of bytes <see cref="ToByteArray"/> will produce.</summary>
    public int SerializedSizeBytes => (PreambleLongs + _hashes.Length) << 3;

    private int PreambleLongs =>
        ThetaPreamble.ComputeCompactPreambleLongs(_empty, _hashes.Length, _thetaLong);

    private bool IsSingleItem => !_empty && _hashes.Length == 1 && _thetaLong == long.MaxValue;

    /// <summary>
    /// Reads a serialized compact Theta sketch, assuming the
    /// <see cref="ThetaSketch.DefaultUpdateSeed"/>.
    /// </summary>
    /// <param name="image">The serialized sketch.</param>
    /// <exception cref="InvalidDataException">The image is malformed, or was built with a different seed.</exception>
    /// <exception cref="NotSupportedException">The image uses the delta-compressed serialization (version 4).</exception>
    public static CompactThetaSketch Deserialize(ReadOnlySpan<byte> image) =>
        Deserialize(image, DefaultUpdateSeed);

    /// <summary>
    /// Reads a serialized compact Theta sketch, validating that it was built
    /// with <paramref name="expectedSeed"/>.
    /// </summary>
    /// <param name="image">The serialized sketch.</param>
    /// <param name="expectedSeed">The update seed the image is expected to carry.</param>
    /// <exception cref="InvalidDataException">The image is malformed, or was built with a different seed.</exception>
    /// <exception cref="NotSupportedException">The image uses the delta-compressed serialization (version 4).</exception>
    public static CompactThetaSketch Deserialize(ReadOnlySpan<byte> image, ulong expectedSeed)
    {
        if (image.Length < 8)
        {
            throw new InvalidDataException(
                $"Theta sketch image must be at least 8 bytes, got {image.Length}.");
        }

        int familyId = ThetaPreamble.ReadFamilyId(image);
        if (familyId != (int)SketchFamily.Compact)
        {
            throw new InvalidDataException(
                $"Expected a Compact Theta sketch (family {(int)SketchFamily.Compact}), got family {familyId}.");
        }

        int serVer = ThetaPreamble.ReadSerVer(image);
        if (serVer == ThetaPreamble.SerVerCompressed)
        {
            throw new NotSupportedException(
                "Delta-compressed Theta sketches (serialization version 4) are not supported yet.");
        }
        if (serVer != ThetaPreamble.SerVer)
        {
            throw new InvalidDataException($"Unrecognized Theta serialization version {serVer}.");
        }

        int preambleLongs = ThetaPreamble.ReadPreambleLongs(image);
        int flags = ThetaPreamble.ReadFlags(image);
        ushort expectedSeedHash = SeedHashes.Compute(expectedSeed);

        // Empty images carry no hashes and store a zero seed hash, so there is
        // nothing to validate against the expected seed.
        if ((flags & ThetaPreamble.EmptyFlagMask) != 0)
        {
            return new CompactThetaSketch([], long.MaxValue, empty: true, ordered: true, expectedSeedHash);
        }

        SeedHashes.Check(ThetaPreamble.ReadSeedHash(image), expectedSeedHash);

        bool single = (flags & ThetaPreamble.SingleItemFlagMask) != 0
            || ThetaPreamble.IsSingleItem(preambleLongs, serVer, familyId, flags);
        if (single)
        {
            if (preambleLongs != 1 || image.Length < 16)
            {
                throw new InvalidDataException(
                    $"Corrupt single-item Theta sketch: preambleLongs={preambleLongs}, length={image.Length}.");
            }

            long hash = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(8));
            return new CompactThetaSketch([hash], long.MaxValue, empty: false, ordered: true, expectedSeedHash);
        }

        if ((flags & ThetaPreamble.CompactFlagMask) == 0)
        {
            throw new InvalidDataException("Corrupt image: a Compact family sketch must have the compact flag set.");
        }
        if ((flags & ThetaPreamble.ReadOnlyFlagMask) == 0)
        {
            throw new InvalidDataException("Corrupt image: a Compact family sketch must have the read-only flag set.");
        }
        if (preambleLongs is not (2 or 3))
        {
            throw new InvalidDataException(
                $"Corrupt image: a non-empty, multi-entry compact sketch needs 2 or 3 preamble longs, got {preambleLongs}.");
        }
        if (image.Length < preambleLongs << 3)
        {
            throw new InvalidDataException(
                $"Truncated image: {image.Length} bytes cannot hold a {preambleLongs}-long preamble.");
        }

        int retained = ThetaPreamble.ReadRetainedEntries(image);
        long thetaLong = preambleLongs > 2 ? ThetaPreamble.ReadThetaLong(image) : long.MaxValue;

        if (retained < 0)
        {
            throw new InvalidDataException($"Corrupt image: negative retained entry count {retained}.");
        }
        if (thetaLong <= 0)
        {
            throw new InvalidDataException($"Corrupt image: theta must be positive, got {thetaLong}.");
        }

        int required = (preambleLongs + retained) << 3;
        if (image.Length < required)
        {
            throw new InvalidDataException(
                $"Truncated image: {retained} entries need {required} bytes, got {image.Length}.");
        }

        long[] hashes = new long[retained];
        ReadOnlySpan<byte> data = image.Slice(preambleLongs << 3, retained << 3);
        for (int i = 0; i < retained; i++)
        {
            hashes[i] = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(i << 3));
        }

        bool ordered = (flags & ThetaPreamble.OrderedFlagMask) != 0;
        return new CompactThetaSketch(hashes, thetaLong, empty: false, ordered, expectedSeedHash);
    }

    /// <inheritdoc/>
    public override byte[] ToByteArray()
    {
        byte[] image = new byte[SerializedSizeBytes];
        Serialize(image);
        return image;
    }

    /// <summary>
    /// Writes the sketch image into <paramref name="destination"/>, which must be
    /// at least <see cref="SerializedSizeBytes"/> long.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public int Serialize(Span<byte> destination)
    {
        int size = SerializedSizeBytes;
        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; the sketch needs {size}.", nameof(destination));
        }

        if (_empty)
        {
            ThetaPreamble.EmptyImage.CopyTo(destination);
            return size;
        }

        int preambleLongs = PreambleLongs;
        int flags = ThetaPreamble.ReadOnlyFlagMask | ThetaPreamble.CompactFlagMask
            | (_ordered ? ThetaPreamble.OrderedFlagMask : 0)
            | (IsSingleItem ? ThetaPreamble.SingleItemFlagMask : 0);

        ThetaPreamble.WriteHeader(destination, preambleLongs, (int)SketchFamily.Compact, flags, _seedHash);

        if (preambleLongs > 1)
        {
            ThetaPreamble.WriteRetainedEntries(destination, _hashes.Length);
            ThetaPreamble.WriteP(destination, 0.0f);
        }
        if (preambleLongs > 2)
        {
            ThetaPreamble.WriteThetaLong(destination, _thetaLong);
        }

        Span<byte> data = destination.Slice(preambleLongs << 3);
        for (int i = 0; i < _hashes.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.Slice(i << 3), _hashes[i]);
        }

        return size;
    }
}
