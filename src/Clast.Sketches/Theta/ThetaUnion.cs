// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Theta;

/// <summary>
/// Merges Theta sketches into one, estimating the distinct count of the union
/// of everything the inputs saw.
/// </summary>
/// <remarks>
/// <para>
/// This is what Theta sketches exist for. Because a sketch's retained sample is
/// determined by hash values alone rather than by insertion order or by which
/// partition a value landed in, sketches built independently — different
/// machines, different days, different engines — can be merged after the fact
/// and the result is as good as one sketch built over all the data. Counting
/// distinct users across a hundred daily Iceberg partitions becomes a hundred
/// cheap merges rather than a rescan.
/// </para>
/// <para>
/// The union keeps its own theta alongside an internal sketch's, because the
/// two fall for different reasons: the union's theta drops to the minimum of
/// every input's theta (a value above some input's theta cannot be known to be
/// absent from it), while the internal sketch's drops as it fills up. The result
/// takes the smaller.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class ThetaUnion
{
    private readonly QuickSelectThetaSketch _gadget;
    private readonly ushort _expectedSeedHash;

    private long _unionThetaLong;
    private bool _unionEmpty;

    /// <summary>
    /// Creates a union.
    /// </summary>
    /// <param name="nominalEntries">
    /// The accuracy parameter <c>k</c> of the result, rounded up to a power of
    /// two. Worth setting at least as high as the largest input sketch's: a
    /// union cannot recover accuracy its own <c>k</c> throws away.
    /// </param>
    /// <param name="seed">
    /// The update seed. Every sketch fed to this union must share it.
    /// </param>
    /// <param name="resizeFactor">How aggressively the internal table grows.</param>
    public ThetaUnion(
        int nominalEntries = ThetaLimits.DefaultNominalEntries,
        ulong seed = ThetaSketch.DefaultUpdateSeed,
        ResizeFactor resizeFactor = ResizeFactor.X8)
    {
        int lgNominalEntries = ThetaLimits.CheckNominalEntries(nominalEntries);
        // The internal sketch is a QuickSelect sketch flagged as the Union family.
        // Alpha is deliberately not an option here: its accuracy advantage comes
        // from decaying theta across its own update stream, which merging discards
        // anyway, and its dirty-table invariants do not survive backdoor inserts.
        _gadget = new QuickSelectThetaSketch(
            lgNominalEntries, seed, samplingProbability: 1.0f, resizeFactor, unionGadget: true);
        _expectedSeedHash = SeedHashes.Compute(seed);
        _unionThetaLong = _gadget.ThetaLong;
        _unionEmpty = _gadget.IsEmpty;
    }

    private ThetaUnion(QuickSelectThetaSketch gadget, ushort expectedSeedHash, long unionThetaLong, bool unionEmpty)
    {
        _gadget = gadget;
        _expectedSeedHash = expectedSeedHash;
        _unionThetaLong = unionThetaLong;
        _unionEmpty = unionEmpty;
    }

    /// <summary>The nominal entry count <c>k</c> bounding the result's size and accuracy.</summary>
    public int NominalEntries => _gadget.NominalEntries;

    /// <summary>The 16-bit hash of the update seed every input must share.</summary>
    public ushort SeedHash => _expectedSeedHash;

    /// <summary>
    /// Merges a sketch in. Null and empty sketches are accepted and change
    /// nothing — the union of anything with the empty set is itself.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The sketch was built with a different update seed, so its hashes are not
    /// comparable with the ones already merged.
    /// </exception>
    public void Union(ThetaSketch? sketch)
    {
        if (sketch is null || sketch.IsEmpty)
        {
            return;
        }

        SeedHashes.Check(sketch.SeedHash, _expectedSeedHash);

        // Theta rule: the result can only be trusted down to the coarsest sample
        // any input took, so take the minimum before inserting anything.
        long sketchTheta = sketch.ThetaLong;
        _unionThetaLong = Math.Min(Math.Min(_unionThetaLong, sketchTheta), _gadget.ThetaLong);
        _unionEmpty = false;

        bool ordered = sketch.IsOrdered;
        long[] cache = sketch.HashCache;
        for (int i = 0; i < cache.Length; i++)
        {
            long hash = cache[i];
            // An update sketch's array is a hash table, so skip empty slots and
            // any entry its own theta has already invalidated.
            if (hash <= 0L || hash >= sketchTheta)
            {
                continue;
            }

            if (hash < _unionThetaLong && hash < _gadget.ThetaLong)
            {
                // Backdoor insert: the value is already hashed, so it bypasses
                // the hash function rather than being hashed a second time.
                _gadget.HashUpdate(hash);
            }
            else if (ordered)
            {
                // Sorted input, and we have passed theta — nothing after this
                // can qualify.
                break;
            }
        }

        _unionThetaLong = Math.Min(_unionThetaLong, _gadget.ThetaLong);
    }

    /// <summary>Merges a serialized compact sketch in.</summary>
    /// <exception cref="InvalidDataException">The image is malformed or was built with a different seed.</exception>
    public void UnionCompactImage(ReadOnlySpan<byte> compactImage) =>
        Union(CompactThetaSketch.Deserialize(compactImage, SeedFromGadget()));

    /// <summary>Returns the merged result in ordered compact form.</summary>
    public CompactThetaSketch GetResult() => GetResult(ordered: true);

    /// <summary>
    /// Returns the merged result in compact form.
    /// </summary>
    /// <param name="ordered">Whether to sort the retained hashes. Ordered is what the reference implementations emit.</param>
    /// <remarks>
    /// Non-destructive: the union can keep accepting sketches afterwards, and
    /// calling this twice gives the same answer.
    /// </remarks>
    public CompactThetaSketch GetResult(bool ordered)
    {
        int gadgetRetained = _gadget.RetainedEntries;
        int k = _gadget.NominalEntries;

        // The selection below reorders whatever array it is handed, so work on a
        // copy — a union stays usable after producing a result.
        long[] cache = (long[])_gadget.HashCache.Clone();

        long gadgetTheta = _gadget.ThetaLong;

        // The internal sketch is allowed to hold more than k between rebuilds;
        // the result must not, so find the theta that trims it back.
        long trimmedTheta = gadgetRetained > k
            ? QuickSelect.SelectExcludingZeros(cache, gadgetRetained, k + 1)
            : gadgetTheta;

        long minTheta = Math.Min(Math.Min(gadgetTheta, trimmedTheta), _unionThetaLong);
        int retainedOut = minTheta < gadgetTheta
            ? ThetaHashTable.CountValid(cache, minTheta)
            : gadgetRetained;

        long[] hashes = ThetaHashTable.CompactCache(cache, retainedOut, minTheta, ordered);

        // Empty only if nothing non-empty was ever merged in.
        bool empty = _gadget.IsEmpty && _unionEmpty;
        return new CompactThetaSketch(hashes, minTheta, empty, ordered, _gadget.SeedHash);
    }

    /// <summary>Returns the union to its initial state, ready to be reused.</summary>
    public void Reset()
    {
        _gadget.Reset();
        _unionThetaLong = _gadget.ThetaLong;
        _unionEmpty = _gadget.IsEmpty;
    }

    /// <summary>
    /// Serializes the union's working state, so a merge can be suspended and
    /// resumed later. To store the answer instead, serialize
    /// <see cref="GetResult()"/>.
    /// </summary>
    public byte[] ToByteArray()
    {
        byte[] image = _gadget.ToByteArray();
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(UnionThetaOffset), _unionThetaLong);

        // The gadget can still look empty while the union is not — merging a
        // sketch that had theta below 1.0 but retained nothing moves the union's
        // theta without inserting anything.
        if (_gadget.IsEmpty != _unionEmpty)
        {
            image[ThetaPreamble.FlagsByte] &= unchecked((byte)~ThetaPreamble.EmptyFlagMask);
        }

        return image;
    }

    /// <summary>Reads a serialized union, assuming the <see cref="ThetaSketch.DefaultUpdateSeed"/>.</summary>
    /// <exception cref="InvalidDataException">The image is malformed or was built with a different seed.</exception>
    public static ThetaUnion Deserialize(ReadOnlySpan<byte> image) =>
        Deserialize(image, ThetaSketch.DefaultUpdateSeed);

    /// <summary>Reads a serialized union, validating that it was built with <paramref name="expectedSeed"/>.</summary>
    /// <exception cref="InvalidDataException">The image is malformed or was built with a different seed.</exception>
    public static ThetaUnion Deserialize(ReadOnlySpan<byte> image, ulong expectedSeed)
    {
        if (image.Length < 32)
        {
            throw new InvalidDataException(
                $"A union image needs at least 32 bytes, got {image.Length}.");
        }

        int familyId = ThetaPreamble.ReadFamilyId(image);
        if (familyId != (int)SketchFamily.Union)
        {
            throw new InvalidDataException(
                $"Expected a Union image (family {(int)SketchFamily.Union}), got family {familyId}.");
        }

        var gadget = (QuickSelectThetaSketch)UpdateThetaSketch.Deserialize(image, expectedSeed);
        long unionTheta = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(UnionThetaOffset));
        if (unionTheta <= 0)
        {
            throw new InvalidDataException($"Corrupt image: union theta must be positive, got {unionTheta}.");
        }

        bool unionEmpty = (ThetaPreamble.ReadFlags(image) & ThetaPreamble.EmptyFlagMask) != 0;
        return new ThetaUnion(gadget, SeedHashes.Compute(expectedSeed), unionTheta, unionEmpty);
    }

    /// <summary>Byte offset of the union's own theta, immediately after the sketch's.</summary>
    private const int UnionThetaOffset = 24;

    private ulong SeedFromGadget() => _gadget.Seed;
}
