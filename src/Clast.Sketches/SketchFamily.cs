// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches;

/// <summary>
/// Identifies the sketch family that produced a serialized image. The numeric
/// values are the family IDs stored in byte 2 of every DataSketches preamble
/// and must not be changed.
/// </summary>
public enum SketchFamily
{
    /// <summary>Theta update sketch, Alpha variant.</summary>
    Alpha = 1,

    /// <summary>Theta update sketch, QuickSelect variant.</summary>
    QuickSelect = 2,

    /// <summary>Compact (immutable, serialized) Theta sketch.</summary>
    Compact = 3,

    /// <summary>Theta union set operation.</summary>
    Union = 4,

    /// <summary>Theta intersection set operation.</summary>
    Intersection = 5,

    /// <summary>Theta A-not-B set operation.</summary>
    AnotB = 6,

    /// <summary>HLL sketch.</summary>
    Hll = 7,

    /// <summary>Classic quantiles sketch.</summary>
    Quantiles = 8,

    /// <summary>Tuple sketch.</summary>
    Tuple = 9,

    /// <summary>Frequent items sketch.</summary>
    Frequency = 10,

    /// <summary>Reservoir sampling sketch.</summary>
    Reservoir = 11,

    /// <summary>Reservoir sampling union.</summary>
    ReservoirUnion = 12,

    /// <summary>VarOpt sampling sketch.</summary>
    VarOpt = 13,

    /// <summary>VarOpt sampling union.</summary>
    VarOptUnion = 14,

    /// <summary>KLL quantiles sketch.</summary>
    Kll = 15,

    /// <summary>CPC sketch.</summary>
    Cpc = 16,

    /// <summary>REQ quantiles sketch.</summary>
    Req = 17,

    /// <summary>Count-min sketch.</summary>
    CountMin = 18,

    /// <summary>EBPPS sampling sketch.</summary>
    Ebpps = 19,

    /// <summary>t-digest.</summary>
    TDigest = 20,

    /// <summary>Bloom filter.</summary>
    BloomFilter = 21,
}
