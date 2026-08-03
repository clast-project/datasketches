// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using Clast.Sketches;
using Clast.Sketches.Theta;

namespace Clast.Sketches.Benchmarks;

/// <summary>
/// Breaks the per-value update cost into its parts: the hash, the table work,
/// and what a warm sketch actually does with most of its input.
/// </summary>
[MemoryDiagnoser]
public class ThetaUpdateBenchmarks
{
    private const int Count = 100_000;
    private const ulong Seed = ThetaSketch.DefaultUpdateSeed;

    /// <summary>Hashing alone, one value at a time, as the sketch does it.</summary>
    [Benchmark(Baseline = true)]
    public long HashOnly()
    {
        long accumulator = 0;
        for (long i = 0; i < Count; i++)
        {
            accumulator ^= (long)(MurmurHash3.Hash(i, Seed).H1 >> 1);
        }
        return accumulator;
    }

    /// <summary>
    /// The same hashes, but four independent chains interleaved. MurmurHash3 is a
    /// long chain of dependent multiplies, so if latency rather than throughput
    /// is the limit, overlapping four of them should be markedly faster per hash.
    /// </summary>
    [Benchmark]
    public long HashFourAtATime()
    {
        long a0 = 0, a1 = 0, a2 = 0, a3 = 0;
        long i = 0;
        for (; i <= Count - 4; i += 4)
        {
            a0 ^= (long)(MurmurHash3.Hash(i, Seed).H1 >> 1);
            a1 ^= (long)(MurmurHash3.Hash(i + 1, Seed).H1 >> 1);
            a2 ^= (long)(MurmurHash3.Hash(i + 2, Seed).H1 >> 1);
            a3 ^= (long)(MurmurHash3.Hash(i + 3, Seed).H1 >> 1);
        }
        for (; i < Count; i++)
        {
            a0 ^= (long)(MurmurHash3.Hash(i, Seed).H1 >> 1);
        }
        return a0 ^ a1 ^ a2 ^ a3;
    }

    /// <summary>The full update path, hash and table together.</summary>
    [Benchmark]
    public UpdateThetaSketch Update()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < Count; i++)
        {
            sketch.Update(i);
        }
        return sketch;
    }

    /// <summary>
    /// Feeding the same value repeatedly. It hashes once per call but is always
    /// a duplicate, so the table probe hits immediately and nothing grows —
    /// isolating hash plus a single successful probe.
    /// </summary>
    [Benchmark]
    public UpdateThetaSketch UpdateAllDuplicates()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < Count; i++)
        {
            sketch.Update(42L);
        }
        return sketch;
    }

    /// <summary>
    /// The same work with the table allocated at full size up front. The growth
    /// schedule is a documented knob, so this measures what choosing it costs or
    /// saves rather than proposing to change the default.
    /// </summary>
    [Benchmark]
    public UpdateThetaSketch UpdateNoResize()
    {
        var sketch = UpdateThetaSketch.Builder().SetResizeFactor(ResizeFactor.X1).Build();
        for (long i = 0; i < Count; i++)
        {
            sketch.Update(i);
        }
        return sketch;
    }

    /// <summary>
    /// A sketch pre-filled so that theta has fallen far: nearly every value is
    /// rejected over theta before the table is touched at all. This is what a
    /// warm sketch spends most of its life doing.
    /// </summary>
    [Benchmark]
    public UpdateThetaSketch UpdateMostlyRejected()
    {
        var sketch = UpdateThetaSketch.Builder().Build();
        for (long i = 0; i < 2_000_000; i++)
        {
            sketch.Update(i);
        }
        return sketch;
    }
}
