// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using Clast.Sketches.Hll;

namespace Clast.Sketches.Benchmarks;

/// <summary>
/// The HLL paths that do bulk work over the register array, which is where any
/// vectorization would pay off.
/// </summary>
[MemoryDiagnoser]
public class HllBenchmarks
{
    private HllSketch _left = null!;
    private HllSketch _right = null!;
    private byte[] _leftImage = null!;

    [Params(12, 16)]
    public int LgConfigK { get; set; }

    [Params(TgtHllType.Hll4, TgtHllType.Hll8)]
    public TgtHllType Type { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _left = Build(0, 1_000_000);
        _right = Build(1_000_000, 1_000_000);
        _leftImage = _left.ToCompactByteArray();
    }

    private HllSketch Build(long start, int count)
    {
        var sketch = new HllSketch(LgConfigK, Type);
        for (long i = 0; i < count; i++)
        {
            sketch.Update(start + i);
        }
        return sketch;
    }

    /// <summary>The per-value update path: hash, coupon, register write.</summary>
    [Benchmark]
    public HllSketch Update()
    {
        var sketch = new HllSketch(LgConfigK, Type);
        for (long i = 0; i < 100_000; i++)
        {
            sketch.Update(i);
        }
        return sketch;
    }

    /// <summary>Register-wise maximum plus the aggregate rebuild it forces.</summary>
    [Benchmark]
    public double UnionTwo()
    {
        var union = new HllUnion(LgConfigK);
        union.Update(_left);
        union.Update(_right);
        return union.Estimate;
    }

    /// <summary>Ten merges against one aggregate rebuild, the realistic ratio.</summary>
    [Benchmark]
    public double UnionTen()
    {
        var union = new HllUnion(LgConfigK);
        for (int i = 0; i < 10; i++)
        {
            union.Update(i % 2 == 0 ? _left : _right);
        }
        return union.Estimate;
    }

    /// <summary>A full register scan through the conversion path.</summary>
    [Benchmark]
    public HllSketch ConvertToHll8() => _left.CopyAs(TgtHllType.Hll8);

    [Benchmark]
    public HllSketch Deserialize() => HllSketch.Deserialize(_leftImage);

    [Benchmark]
    public byte[] Serialize() => _left.ToCompactByteArray();

    /// <summary>The composite estimator, which the union's results always use.</summary>
    [Benchmark]
    public double CompositeEstimate() => _left.CompositeEstimate;
}
