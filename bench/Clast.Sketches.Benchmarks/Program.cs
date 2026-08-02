// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Clast.Sketches;

// Reflection rather than typeof(...): top-level statements already define
// Program, and naming any one benchmark class here would hide the others.
BenchmarkSwitcher.FromAssembly(System.Reflection.Assembly.GetExecutingAssembly()).Run(args);

[MemoryDiagnoser]
public class MurmurHash3Benchmarks
{
    private byte[] _bytes = null!;
    private long[] _longs = null!;

    [Params(8, 40, 1024)]
    public int LengthBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[LengthBytes];
        new Random(42).NextBytes(_bytes);

        _longs = new long[LengthBytes / sizeof(long)];
        for (int i = 0; i < _longs.Length; i++)
        {
            _longs[i] = BitConverter.ToInt64(_bytes, i * sizeof(long));
        }
    }

    [Benchmark]
    public Hash128 HashBytes() => MurmurHash3.Hash(_bytes, seed: 9001);

    [Benchmark]
    public Hash128 HashLongs() => MurmurHash3.Hash(_longs.AsSpan(), seed: 9001);

    [Benchmark]
    public Hash128 HashScalarLong() => MurmurHash3.Hash(0x0123456789abcdefL, seed: 9001);
}
