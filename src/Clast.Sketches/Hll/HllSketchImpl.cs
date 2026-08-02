// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Hll;

/// <summary>
/// The state behind an <see cref="HllSketch"/>, which swaps implementations as
/// the sketch grows through its modes.
/// </summary>
/// <remarks>
/// <see cref="CouponUpdate"/> returns the implementation to use next, which is
/// usually <c>this</c> but is a promoted instance at a mode boundary. That is
/// why the public sketch holds a mutable reference rather than deriving from
/// this type.
/// </remarks>
internal abstract class HllSketchImpl
{
    protected HllSketchImpl(int lgConfigK, TgtHllType tgtHllType, HllCurMode curMode)
    {
        LgConfigK = lgConfigK;
        TgtHllType = tgtHllType;
        CurMode = curMode;
    }

    public int LgConfigK { get; }

    public TgtHllType TgtHllType { get; }

    public HllCurMode CurMode { get; }

    public abstract bool IsEmpty { get; }

    /// <summary>
    /// True if the sketch's state no longer supports the HIP estimator.
    /// </summary>
    /// <remarks>
    /// HIP works by accumulating an increment at each update, which requires
    /// seeing every update in order. A sketch assembled by merging has not, so it
    /// falls back to the composite estimator.
    /// </remarks>
    public abstract bool IsOutOfOrder { get; }

    public abstract double Estimate { get; }

    public abstract double CompositeEstimate { get; }

    public abstract double HipEstimate { get; }

    public abstract double GetLowerBound(int numStdDev);

    public abstract double GetUpperBound(int numStdDev);

    /// <summary>Number of preamble ints this mode serializes with.</summary>
    public abstract int PreInts { get; }

    /// <summary>Byte offset at which this mode's data begins.</summary>
    public abstract int DataStart { get; }

    public abstract int CompactSerializationBytes { get; }

    public abstract int UpdatableSerializationBytes { get; }

    /// <summary>
    /// Folds a coupon in, returning the implementation that should be used from
    /// now on — a different one when the update triggers a mode promotion.
    /// </summary>
    public abstract HllSketchImpl CouponUpdate(int coupon);

    public abstract HllSketchImpl Copy();

    public abstract byte[] ToCompactByteArray();

    public abstract byte[] ToUpdatableByteArray();
}
