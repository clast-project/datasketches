// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using Clast.Sketches;
using Clast.Sketches.Theta;

namespace Clast.Sketches.Benchmarks;

[MemoryDiagnoser]
public class ThetaBenchmarks
{
    private byte[] _compactImage = null!;
    private CompactThetaSketch _compact = null!;

    [Params(1_000, 100_000, 1_000_000)]
    public int DistinctValues { get; set; }

    [Params(SketchFamily.QuickSelect, SketchFamily.Alpha)]
    public SketchFamily Family { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sketch = Build();
        _compact = sketch.Compact();
        _compactImage = _compact.ToByteArray();
    }

    private UpdateThetaSketch Build()
    {
        var sketch = UpdateThetaSketch.Builder().SetFamily(Family).Build();
        for (long i = 0; i < DistinctValues; i++)
        {
            sketch.Update(i);
        }
        return sketch;
    }

    [Benchmark]
    public UpdateThetaSketch UpdateDistinct() => Build();

    [Benchmark]
    public CompactThetaSketch CompactOrdered() => Build().Compact();

    [Benchmark]
    public byte[] Serialize() => _compact.ToByteArray();

    [Benchmark]
    public CompactThetaSketch Deserialize() => CompactThetaSketch.Deserialize(_compactImage);
}
