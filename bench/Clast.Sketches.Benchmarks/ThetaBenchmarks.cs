// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using Clast.Sketches;
using Clast.Sketches.Theta;

namespace Clast.Sketches.Benchmarks;

/// <summary>
/// Theta operations, each measured against pre-built inputs so that sketch
/// construction does not mask the operation under test.
/// </summary>
[MemoryDiagnoser]
public class ThetaBenchmarks
{
    private UpdateThetaSketch _updatable = null!;
    private CompactThetaSketch _compactA = null!;
    private CompactThetaSketch _compactB = null!;
    private byte[] _image = null!;
    private byte[] _compressedImage = null!;

    [Params(100_000, 1_000_000)]
    public int DistinctValues { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _updatable = Build(0, DistinctValues);
        _compactA = _updatable.Compact();
        _compactB = Build(DistinctValues, DistinctValues).Compact();
        _image = _compactA.ToByteArray();
        _compressedImage = _compactA.ToByteArrayCompressed();
    }

    private static UpdateThetaSketch Build(long start, int count)
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch;
    }

    /// <summary>Per-value update: hash, probe, occasional resize or rebuild.</summary>
    [Benchmark]
    public UpdateThetaSketch Update() => Build(0, 100_000);

    /// <summary>Filters the hash table into a gap-free array and sorts it.</summary>
    [Benchmark]
    public CompactThetaSketch CompactOrdered() => _updatable.Compact(ordered: true);

    /// <summary>The same filter without the sort, isolating the compaction.</summary>
    [Benchmark]
    public CompactThetaSketch CompactUnordered() => _updatable.Compact(ordered: false);

    [Benchmark]
    public byte[] Serialize() => _compactA.ToByteArray();

    [Benchmark]
    public CompactThetaSketch Deserialize() => CompactThetaSketch.Deserialize(_image);

    [Benchmark]
    public byte[] SerializeCompressed() => _compactA.ToByteArrayCompressed();

    [Benchmark]
    public CompactThetaSketch DeserializeCompressed() => CompactThetaSketch.Deserialize(_compressedImage);

    /// <summary>Union of two compact sketches, including the result extraction.</summary>
    [Benchmark]
    public double Union()
    {
        var union = new ThetaUnion();
        union.Union(_compactA);
        union.Union(_compactB);
        return union.GetResult().Estimate;
    }

    [Benchmark]
    public double Intersect() => ThetaIntersection.Of(_compactA, _compactB).Estimate;

    [Benchmark]
    public double AnotB() => ThetaAnotB.Of(_compactA, _compactB).Estimate;
}
