// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using Clast.Sketches.Theta;

namespace Clast.Sketches.Benchmarks;

/// <summary>
/// Isolates the sort that dominates <c>Compact(ordered: true)</c>, over exactly
/// the data a real sketch produces: retained hashes in hash-table slot order.
/// </summary>
[MemoryDiagnoser]
public class ThetaSortBenchmarks
{
    private long[] _unsorted = null!;
    private long[] _scratch = null!;
    private long _theta;

    [Params(4096, 6560, 8192)]
    public int Retained { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Take the real retained set from a real sketch, in the order compaction
        // produces it, so the benchmark sees the distribution the sort actually
        // faces rather than synthetic uniform noise.
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 2_000_000; i++)
        {
            sketch.Update(i);
        }

        var compact = sketch.Compact(ordered: false);
        _theta = compact.ThetaLong;
        long[] hashes = compact.HashValues.ToArray();
        _unsorted = new long[Retained];
        // The sketch retains a few thousand; take as many as this case needs,
        // repeating from the start if it asks for more than one pass provides.
        for (int i = 0; i < Retained; i++)
        {
            _unsorted[i] = hashes[i % hashes.Length];
        }
        _scratch = new long[Retained];
    }

    [Benchmark(Baseline = true)]
    public long ArraySort()
    {
        _unsorted.CopyTo(_scratch, 0);
        Array.Sort(_scratch);
        return _scratch[0];
    }

    [Benchmark]
    public long DistributionSort()
    {
        _unsorted.CopyTo(_scratch, 0);
        ThetaSort.Sort(_scratch, Retained, _theta);
        return _scratch[0];
    }

    /// <summary>The copy alone, so the sort figures can be read net of it.</summary>
    [Benchmark]
    public long CopyOnly()
    {
        _unsorted.CopyTo(_scratch, 0);
        return _scratch[0];
    }
}
