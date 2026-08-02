// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Sketches.Hll;

/// <summary>
/// A HyperLogLog sketch: estimates how many distinct values it has seen, in
/// space that grows with the configured accuracy rather than with the data.
/// </summary>
/// <remarks>
/// <para>
/// This is the Apache DataSketches HLL, not the HyperLogLog++ of the Google
/// paper — same family, different algorithm and a different serialized form. It
/// is what Spark's <c>hll_sketch_agg</c> produces.
/// </para>
/// <para>
/// Compared with <see cref="Theta.ThetaSketch"/>: HLL is markedly more compact
/// for the same accuracy, so it is the better choice when all you need is a
/// distinct count. Theta earns its extra space by supporting intersection and
/// set difference, which HLL cannot do.
/// </para>
/// <para>
/// The sketch changes representation as it fills — a coupon list, then a hash
/// set, then the register array — so a sketch of a handful of values costs a
/// handful of bytes. This is handled internally and is visible only in the
/// serialized size.
/// </para>
/// <para>
/// Instances are not thread-safe.
/// </para>
/// </remarks>
public sealed class HllSketch
{
    private HllSketchImpl _impl;

    /// <summary>
    /// Creates an empty sketch.
    /// </summary>
    /// <param name="lgConfigK">
    /// Base-2 logarithm of the register count <c>k</c>, between 4 and 21.
    /// Relative error is about <c>1/sqrt(k)</c>, so the default of 12 (4096
    /// registers) gives roughly 1.6%.
    /// </param>
    /// <param name="tgtHllType">The register width to use once the sketch reaches HLL mode.</param>
    public HllSketch(int lgConfigK = HllUtil.DefaultLgK, TgtHllType tgtHllType = TgtHllType.Hll4)
    {
        HllUtil.CheckLgK(lgConfigK);
        if (!Enum.IsDefined(typeof(TgtHllType), tgtHllType))
        {
            throw new ArgumentOutOfRangeException(nameof(tgtHllType), tgtHllType, "Unknown HLL target type.");
        }
        _impl = new CouponList(lgConfigK, tgtHllType, HllCurMode.List);
    }

    private HllSketch(HllSketchImpl impl) => _impl = impl;

    /// <summary>Base-2 logarithm of the register count.</summary>
    public int LgConfigK => _impl.LgConfigK;

    /// <summary>The register count <c>k</c>.</summary>
    public int ConfigK => 1 << _impl.LgConfigK;

    /// <summary>The register width this sketch uses in HLL mode.</summary>
    public TgtHllType TgtHllType => _impl.TgtHllType;

    /// <summary>True if the sketch has seen no values.</summary>
    public bool IsEmpty => _impl.IsEmpty;

    /// <summary>The estimated number of distinct values.</summary>
    public double Estimate => _impl.Estimate;

    /// <summary>
    /// The composite (non-HIP) estimate.
    /// </summary>
    /// <remarks>
    /// <see cref="Estimate"/> normally uses the more accurate HIP estimator,
    /// which is only available when the sketch observed every update itself.
    /// This one always applies and is what a merged sketch falls back to.
    /// </remarks>
    public double CompositeEstimate => _impl.CompositeEstimate;

    /// <summary>Bytes <see cref="ToCompactByteArray"/> will produce.</summary>
    public int CompactSerializationBytes => _impl.CompactSerializationBytes;

    /// <summary>Bytes <see cref="ToUpdatableByteArray"/> will produce.</summary>
    public int UpdatableSerializationBytes => _impl.UpdatableSerializationBytes;

    /// <summary>
    /// A lower confidence bound on the distinct count.
    /// </summary>
    /// <param name="numStdDev">1, 2, or 3 standard deviations — roughly 68%, 95%, or 99.7%. Defaults to 2.</param>
    public double GetLowerBound(int numStdDev = 2) => _impl.GetLowerBound(numStdDev);

    /// <summary>
    /// An upper confidence bound on the distinct count.
    /// </summary>
    /// <param name="numStdDev">1, 2, or 3 standard deviations — roughly 68%, 95%, or 99.7%. Defaults to 2.</param>
    public double GetUpperBound(int numStdDev = 2) => _impl.GetUpperBound(numStdDev);

    /// <summary>Presents a 64-bit integer to the sketch.</summary>
    public void Update(long datum) => CouponUpdate(CouponOf(MurmurHash3.Hash(datum, ThetaSketchSeed)));

    /// <summary>
    /// Presents a floating-point value. Negative zero folds into positive zero
    /// and every NaN encoding into one, so values that compare equal count as
    /// equal.
    /// </summary>
    public void Update(double datum)
    {
        // Java's Double.doubleToLongBits canonicalizes NaN; BitConverter does not.
        long bits = double.IsNaN(datum)
            ? 0x7FF8000000000000L
            : BitConverter.DoubleToInt64Bits(datum == 0.0 ? 0.0 : datum);
        CouponUpdate(CouponOf(MurmurHash3.Hash(bits, ThetaSketchSeed)));
    }

    /// <summary>
    /// Presents a string, hashed as its UTF-8 bytes. Null and empty strings are
    /// ignored.
    /// </summary>
    public void Update(string? datum)
    {
        if (string.IsNullOrEmpty(datum)) { return; }
        CouponUpdate(CouponOf(MurmurHash3.Hash(Encoding.UTF8.GetBytes(datum!), ThetaSketchSeed)));
    }

    /// <summary>Presents a byte sequence. An empty span is ignored.</summary>
    public void Update(ReadOnlySpan<byte> datum)
    {
        if (datum.IsEmpty) { return; }
        CouponUpdate(CouponOf(MurmurHash3.Hash(datum, ThetaSketchSeed)));
    }

    /// <summary>Presents a sequence of 64-bit integers as a single value. An empty span is ignored.</summary>
    public void Update(ReadOnlySpan<long> datum)
    {
        if (datum.IsEmpty) { return; }
        CouponUpdate(CouponOf(MurmurHash3.Hash(datum, ThetaSketchSeed)));
    }

    /// <summary>Returns an independent copy of this sketch.</summary>
    public HllSketch Copy() => new(_impl.Copy());

    /// <summary>
    /// Serializes to the compact form — the one to persist, and what
    /// <c>toCompactByteArray</c> produces in the reference implementations.
    /// </summary>
    public byte[] ToCompactByteArray() => _impl.ToCompactByteArray();

    /// <summary>
    /// Serializes to the updatable form, which stores whole tables so the image
    /// can be resumed without rebuilding.
    /// </summary>
    public byte[] ToUpdatableByteArray() => _impl.ToUpdatableByteArray();

    /// <summary>Reads a serialized HLL sketch in either form.</summary>
    /// <exception cref="InvalidDataException">The image is malformed.</exception>
    public static HllSketch Deserialize(ReadOnlySpan<byte> image) => new(HllSerialization.Deserialize(image));

    /// <summary>
    /// The update seed. HLL has no seed hash in its preamble and no configurable
    /// seed, but it hashes with the same constant the rest of the library uses.
    /// </summary>
    private const ulong ThetaSketchSeed = Theta.ThetaSketch.DefaultUpdateSeed;

    private void CouponUpdate(int coupon)
    {
        // A zero value means the hash produced no usable register value, which
        // cannot happen for real input but is cheap to guard.
        if (HllUtil.PairValue(coupon) == HllUtil.Empty) { return; }
        _impl = _impl.CouponUpdate(coupon);
    }

    /// <summary>
    /// Derives a coupon from a 128-bit hash: the low 26 bits of one half select
    /// the register, and the leading-zero count of the other half — plus one —
    /// becomes the value.
    /// </summary>
    private static int CouponOf(Hash128 hash)
    {
        int addr26 = (int)(hash.H1 & HllUtil.KeyMask26);
        int lz = HllUtil.NumberOfLeadingZeros(hash.H2);
        int value = Math.Min(lz, 62) + 1;
        return (value << HllUtil.KeyBits26) | addr26;
    }
}
