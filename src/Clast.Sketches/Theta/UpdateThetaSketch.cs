// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Sketches.Theta;

/// <summary>
/// The outcome of presenting a value to an update sketch.
/// </summary>
public enum ThetaUpdateResult
{
    /// <summary>The value was new and is now retained.</summary>
    Inserted,

    /// <summary>The value hashes to something the sketch already holds.</summary>
    RejectedDuplicate,

    /// <summary>The value's hash landed at or above theta, so the sketch discards it.</summary>
    RejectedOverTheta,

    /// <summary>The value was null or empty, which carries no information to hash.</summary>
    RejectedNullOrEmpty,
}

/// <summary>
/// A mutable Theta sketch that accepts values. Call <see cref="Compact()"/> to
/// get the immutable, serializable form.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>Update</c> overload reduces its argument to 64 bits of MurmurHash3
/// under the sketch's seed, keeps the low 63 bits, and offers that to the hash
/// table. Two consequences worth knowing: the sketch counts <em>distinct hash
/// values</em>, so two different inputs that hash alike count once; and the
/// numeric overloads agree with the reference implementations, so a sketch
/// built here over the integers 0..n-1 is byte-identical to one built by
/// <c>datasketches-java</c> over the same values.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public abstract class UpdateThetaSketch : ThetaSketch
{
    private readonly ulong _seed;
    private readonly ushort _seedHash;
    private readonly int _lgNominalEntries;
    private readonly float _samplingProbability;
    private readonly ResizeFactor _resizeFactor;

    private protected UpdateThetaSketch(
        int lgNominalEntries, ulong seed, float samplingProbability, ResizeFactor resizeFactor)
    {
        _lgNominalEntries = Math.Max(lgNominalEntries, ThetaLimits.MinLgNominalEntries);
        _seed = seed;
        // Computed once: a union checks it on every incoming sketch.
        _seedHash = SeedHashes.Compute(seed);
        _samplingProbability = samplingProbability;
        _resizeFactor = resizeFactor;
    }

    /// <summary>Creates a builder with the reference defaults: 4096 nominal entries, QuickSelect, seed 9001.</summary>
    public static UpdateThetaSketchBuilder Builder() => new();

    /// <summary>The update seed. Sketches with different seeds cannot be merged.</summary>
    public ulong Seed => _seed;

    /// <summary>Base-2 logarithm of the nominal entry count <c>k</c>.</summary>
    public int LgNominalEntries => _lgNominalEntries;

    /// <summary>The nominal entry count <c>k</c>, which sets the sketch's accuracy and size.</summary>
    public int NominalEntries => 1 << _lgNominalEntries;

    /// <summary>The up-front sampling probability. 1.0 unless explicitly configured lower.</summary>
    public float SamplingProbability => _samplingProbability;

    /// <summary>How aggressively the internal hash table grows.</summary>
    public ResizeFactor ResizeFactor => _resizeFactor;

    /// <inheritdoc/>
    public override ushort SeedHash => _seedHash;

    /// <summary>Presents a 64-bit integer to the sketch.</summary>
    public ThetaUpdateResult Update(long datum) => HashUpdate(HashOf(datum));

    /// <summary>
    /// Presents a floating-point value to the sketch. Negative zero is folded
    /// into positive zero and every NaN bit pattern into one canonical NaN, so
    /// values that compare equal also count as equal.
    /// </summary>
    public ThetaUpdateResult Update(double datum)
    {
        // Java's Double.doubleToLongBits collapses every NaN encoding to this one
        // value, and the reference sketches inherit that. BitConverter does not —
        // it is the equivalent of doubleToRawLongBits — so canonicalize here or a
        // signalling NaN would count as a distinct value from a quiet one.
        long bits = double.IsNaN(datum)
            ? 0x7FF8000000000000L
            : BitConverter.DoubleToInt64Bits(datum == 0.0 ? 0.0 : datum);
        return HashUpdate(HashOf(bits));
    }

    /// <summary>
    /// Presents a string, hashed as its UTF-8 bytes. Null and empty strings are
    /// rejected without changing the sketch.
    /// </summary>
    public ThetaUpdateResult Update(string? datum)
    {
        if (string.IsNullOrEmpty(datum))
        {
            return ThetaUpdateResult.RejectedNullOrEmpty;
        }

        // Hashing the UTF-8 encoding is what makes a string sketch portable; the
        // Java reference does the same, so \"abc\" hashes identically there.
        byte[] utf8 = Encoding.UTF8.GetBytes(datum!);
        return HashUpdate(HashOf(utf8));
    }

    /// <summary>Presents a byte sequence. An empty span is rejected without changing the sketch.</summary>
    public ThetaUpdateResult Update(ReadOnlySpan<byte> datum) =>
        datum.IsEmpty ? ThetaUpdateResult.RejectedNullOrEmpty : HashUpdate(HashOf(datum));

    /// <summary>Presents a sequence of 64-bit integers as a single value. An empty span is rejected.</summary>
    public ThetaUpdateResult Update(ReadOnlySpan<long> datum) =>
        datum.IsEmpty
            ? ThetaUpdateResult.RejectedNullOrEmpty
            : HashUpdate((long)(MurmurHash3.Hash(datum, _seed).H1 >> 1));

    /// <summary>
    /// Returns the immutable, ordered compact form of this sketch — the one that
    /// serializes.
    /// </summary>
    public CompactThetaSketch Compact() => Compact(ordered: true);

    /// <summary>
    /// Returns the immutable compact form of this sketch.
    /// </summary>
    /// <param name="ordered">
    /// Whether to sort the retained hashes. Ordered is the default and what the
    /// reference implementations produce; unordered saves the sort when the
    /// result is about to be fed straight into a set operation.
    /// </param>
    public CompactThetaSketch Compact(bool ordered)
    {
        int retained = RetainedEntries;
        long thetaLong = ThetaLong;
        long[] hashes = ThetaHashTable.CompactCache(HashCache, retained, thetaLong, ordered);
        return new CompactThetaSketch(hashes, thetaLong, IsEmpty, ordered, SeedHash);
    }

    /// <summary>
    /// Trims the sketch back to at most <see cref="NominalEntries"/> retained
    /// values, lowering theta if needed.
    /// </summary>
    /// <remarks>
    /// An update sketch is allowed to hold more than <c>k</c> entries between
    /// rebuilds — that slack is what keeps updates cheap. Calling this yields
    /// the smallest serialized form at the cost of a little accuracy.
    /// </remarks>
    public abstract UpdateThetaSketch Rebuild();

    /// <summary>Returns the sketch to its initial empty state, reusing its storage where possible.</summary>
    public abstract void Reset();

    /// <summary>Number of preamble longs this sketch serializes with.</summary>
    internal virtual int CurrentPreambleLongs => 3;

    /// <summary>Base-2 logarithm of the current hash table length.</summary>
    internal abstract int LgArrLongs { get; }

    /// <summary>True if the table may hold entries at or above theta that have not been swept out yet.</summary>
    internal abstract bool IsDirty { get; }

    /// <summary>Offers an already-computed 63-bit hash to the sketch.</summary>
    internal abstract ThetaUpdateResult HashUpdate(long hash);

    /// <summary>Replaces this sketch's state with one read from a serialized image.</summary>
    internal abstract void LoadState(int lgArrLongs, int retained, long thetaLong, bool empty, long[] cache);

    /// <summary>
    /// Reads a serialized update sketch, assuming the
    /// <see cref="ThetaSketch.DefaultUpdateSeed"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The image is malformed, or was built with a different seed.</exception>
    public static UpdateThetaSketch Deserialize(ReadOnlySpan<byte> image) =>
        Deserialize(image, DefaultUpdateSeed);

    /// <summary>
    /// Reads a serialized update sketch, validating that it was built with
    /// <paramref name="expectedSeed"/>.
    /// </summary>
    /// <remarks>
    /// Unlike a compact image, this carries the whole hash table, so the
    /// resulting sketch can keep accepting values.
    /// </remarks>
    /// <exception cref="InvalidDataException">The image is malformed, or was built with a different seed.</exception>
    public static UpdateThetaSketch Deserialize(ReadOnlySpan<byte> image, ulong expectedSeed)
    {
        if (image.Length < 24)
        {
            throw new InvalidDataException(
                $"An update sketch image needs at least 24 bytes, got {image.Length}.");
        }

        int serVer = ThetaPreamble.ReadSerVer(image);
        if (serVer != ThetaPreamble.SerVer)
        {
            throw new InvalidDataException($"Unrecognized Theta serialization version {serVer}.");
        }

        int familyId = ThetaPreamble.ReadFamilyId(image);
        SketchFamily family = (SketchFamily)familyId;
        if (family is not (SketchFamily.QuickSelect or SketchFamily.Alpha or SketchFamily.Union))
        {
            throw new InvalidDataException(
                $"Family {familyId} is not an updatable Theta sketch; use CompactThetaSketch.Deserialize for compact images.");
        }

        int preambleLongs = ThetaPreamble.ReadPreambleLongs(image);
        int expectedPreambleLongs = family == SketchFamily.Union ? 4 : 3;
        if (preambleLongs != expectedPreambleLongs)
        {
            throw new InvalidDataException(
                $"Corrupt image: {family} needs {expectedPreambleLongs} preamble longs, got {preambleLongs}.");
        }

        int flags = ThetaPreamble.ReadFlags(image);
        int forbidden = ThetaPreamble.OrderedFlagMask | ThetaPreamble.CompactFlagMask | ThetaPreamble.ReadOnlyFlagMask;
        if ((flags & forbidden) != 0)
        {
            throw new InvalidDataException(
                "Corrupt image: an update sketch cannot be compact, ordered, or read-only.");
        }

        SeedHashes.Check(ThetaPreamble.ReadSeedHash(image), SeedHashes.Compute(expectedSeed));

        int lgNomLongs = ThetaPreamble.ReadLgNomLongs(image);
        int lgArrLongs = ThetaPreamble.ReadLgArrLongs(image);
        if (lgNomLongs < ThetaLimits.MinLgNominalEntries || lgNomLongs > ThetaLimits.MaxLgNominalEntries)
        {
            throw new InvalidDataException($"Corrupt image: lgNomLongs {lgNomLongs} is out of range.");
        }
        if (lgArrLongs < ThetaLimits.MinLgArrLongs || lgArrLongs > ThetaLimits.MaxLgNominalEntries + 1)
        {
            throw new InvalidDataException($"Corrupt image: lgArrLongs {lgArrLongs} is out of range.");
        }

        int arrLongs = 1 << lgArrLongs;
        int required = (preambleLongs + arrLongs) << 3;
        if (image.Length < required)
        {
            throw new InvalidDataException(
                $"Truncated image: a {arrLongs}-slot table needs {required} bytes, got {image.Length}.");
        }

        float p = ThetaPreamble.ReadP(image);
        int retained = ThetaPreamble.ReadRetainedEntries(image);
        long thetaLong = ThetaPreamble.ReadThetaLong(image);
        bool empty = (flags & ThetaPreamble.EmptyFlagMask) != 0;

        if (thetaLong <= 0)
        {
            throw new InvalidDataException($"Corrupt image: theta must be positive, got {thetaLong}.");
        }
        if (retained < 0 || retained > arrLongs)
        {
            throw new InvalidDataException($"Corrupt image: retained count {retained} does not fit a {arrLongs}-slot table.");
        }
        // While the table is still growing, theta cannot have dropped below p —
        // nothing has forced it down yet.
        if (lgArrLongs <= lgNomLongs && thetaLong / (double)long.MaxValue < p)
        {
            throw new InvalidDataException(
                $"Corrupt image: theta is below p while the table is still under full size.");
        }

        // A table size that is not reachable from the minimum by repeated
        // application of the stored resize factor means the factor is wrong;
        // X2 can reach every size, so fall back to it rather than reject.
        int lgResizeFactor = ThetaPreamble.ReadLgResizeFactor(image);
        ResizeFactor resizeFactor = IsResizeFactorConsistent(lgNomLongs, lgArrLongs, lgResizeFactor)
            ? ResizeFactorExtensions.FromLg(lgResizeFactor)
            : ResizeFactor.X2;

        long[] cache = new long[arrLongs];
        ReadOnlySpan<byte> data = image.Slice(preambleLongs << 3, arrLongs << 3);
        for (int i = 0; i < arrLongs; i++)
        {
            cache[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Slice(i << 3));
        }

        UpdateThetaSketch sketch = family == SketchFamily.Alpha
            ? new AlphaThetaSketch(lgNomLongs, expectedSeed, p, resizeFactor)
            : new QuickSelectThetaSketch(
                lgNomLongs, expectedSeed, p, resizeFactor, unionGadget: family == SketchFamily.Union);

        sketch.LoadState(lgArrLongs, retained, thetaLong, empty, cache);
        return sketch;
    }

    private static bool IsResizeFactorConsistent(int lgNomLongs, int lgArrLongs, int lgResizeFactor)
    {
        int lgTarget = lgNomLongs + 1;
        return lgResizeFactor == 0
            ? lgArrLongs == lgTarget
            : (lgTarget - lgArrLongs) % lgResizeFactor == 0;
    }

    /// <inheritdoc/>
    public override byte[] ToByteArray()
    {
        // A dirty table would serialize entries that are no longer below theta,
        // and readers count them; sweep first so the image is self-consistent.
        if (IsDirty)
        {
            Rebuild();
        }

        int retained = RetainedEntries;
        if (IsEmpty && retained != 0)
        {
            throw new InvalidOperationException(
                $"Corrupt state: a sketch flagged empty cannot retain {retained} entries.");
        }

        int preambleLongs = CurrentPreambleLongs;
        int arrLongs = 1 << LgArrLongs;
        byte[] image = new byte[(preambleLongs + arrLongs) << 3];

        int flags = IsEmpty ? ThetaPreamble.EmptyFlagMask : 0;
        ThetaPreamble.WriteHeader(
            image,
            preambleLongs,
            _resizeFactor.Lg(),
            (int)Family,
            _lgNominalEntries,
            LgArrLongs,
            flags,
            SeedHash);

        ThetaPreamble.WriteRetainedEntries(image, retained);
        ThetaPreamble.WriteP(image, _samplingProbability);
        // An empty sketch is theta = 1.0 even if p < 1.0 set theta lower; readers
        // reject the combination of the empty flag and theta < 1.0.
        ThetaPreamble.WriteThetaLong(image, IsEmpty && retained == 0 ? long.MaxValue : ThetaLong);

        Span<byte> data = image.AsSpan(preambleLongs << 3);
        long[] cache = HashCache;
        for (int i = 0; i < arrLongs; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.Slice(i << 3), cache[i]);
        }

        return image;
    }

    private long HashOf(long datum) => (long)(MurmurHash3.Hash(datum, _seed).H1 >> 1);

    private long HashOf(ReadOnlySpan<byte> datum) => (long)(MurmurHash3.Hash(datum, _seed).H1 >> 1);
}
