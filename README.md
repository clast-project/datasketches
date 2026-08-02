# Clast.Sketches

Streaming sketches for .NET, binary-compatible with [Apache DataSketches](https://datasketches.apache.org/).

[![NuGet](https://img.shields.io/nuget/v/Clast.Sketches.svg)](https://www.nuget.org/packages/Clast.Sketches/)
[![CI](https://github.com/clast-project/datasketches/actions/workflows/ci.yml/badge.svg)](https://github.com/clast-project/datasketches/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/clast-project/datasketches/blob/main/LICENSE)

## Overview

`Clast.Sketches` is a from-scratch C# implementation of the Apache DataSketches
algorithms and, critically, of their **serialized forms**. A sketch written by
`datasketches-java`, `datasketches-cpp`, Spark, or Trino reads here, and one
written here reads there. That portability is the point: sketches are usually
produced by one engine and consumed by another.

The initial target is the sketches that show up in [Apache Iceberg Puffin
files](https://iceberg.apache.org/puffin-spec/) — Theta for the
`apache-datasketches-theta-v1` blob, and HLL for engines that store NDV that way.

## Status

Early. Under construction; not yet published.

| Component | State |
| --- | --- |
| MurmurHash3 x64 128 | Done — matches the Java reference bit for bit |
| Compact Theta sketch: read, estimate, serialize | Done — round-trips the TCK snapshots byte for byte |
| Theta update sketch, QuickSelect | Done — reproduces the TCK snapshots byte for byte from scratch |
| Theta update sketch, Alpha (required by Puffin) | Done |
| Theta union, intersection, A-not-B | Done |
| Theta error bounds | Done — matches the reference to 1e-15 across ~38M evaluations |
| HLL sketch (`HLL_4` / `HLL_6` / `HLL_8`) | Done — reproduces all 24 TCK snapshots byte for byte |
| HLL union | Done |
| Delta-compressed Theta (serialization version 4) | Done — reproduces the TCK snapshots byte for byte |

Compatibility is tested against [apache/datasketches-tck](https://github.com/apache/datasketches-tck),
the project's own cross-language serialization snapshots — the same images the
Java, C++, and Go implementations validate against.

## Example

```csharp
using Clast.Sketches.Theta;

// Count distinct values.
var sketch = UpdateThetaSketch.Builder().Build();
foreach (var value in values)
    sketch.Update(value);

Console.WriteLine(sketch.Estimate);

// Serialize to the format Iceberg Puffin stores in an
// `apache-datasketches-theta-v1` blob.
byte[] blob = sketch.Compact().ToByteArray();

// Read one back — written by us, by Spark, by Trino, by anything.
var loaded = CompactThetaSketch.Deserialize(blob);
Console.WriteLine(loaded.Estimate);
```

Every sketch can report how much to trust its estimate:

```csharp
// ~95% confidence by default; pass 1 or 3 for ~68% or ~99.7%.
Console.WriteLine($"{sketch.GetLowerBound()} .. {sketch.GetUpperBound()}");
```

Sketches built independently merge exactly — which is the reason to use Theta
over a plain counter. Counting distinct values across a hundred Iceberg
partitions becomes a hundred cheap merges instead of a rescan:

```csharp
var union = new ThetaUnion(nominalEntries: 4096);
foreach (var blob in puffinBlobs)
    union.UnionCompactImage(blob);

Console.WriteLine(union.GetResult().Estimate);
```

Theta sketches also have a delta-compressed serialization, typically 30-40%
smaller. Ordered hashes sit fairly evenly below theta, so the gaps between them
need far fewer bits than the hashes themselves:

```csharp
byte[] smaller = sketch.Compact().ToByteArrayCompressed();

// Deserialize reads either form — the image says which it is.
var loaded = CompactThetaSketch.Deserialize(smaller);
```

Intersection and set difference work too — and unlike a union, they cannot be
computed from the estimates alone, only from the sketches:

```csharp
var shared  = ThetaIntersection.Of(monday, tuesday);   // seen on both days
var newToday = ThetaAnotB.Of(tuesday, monday);         // seen only on Tuesday
```

Their results carry wider relative error than their operands, since a small
intersection is recovered from two large sketches. Check the bounds before
trusting a near-empty result.

Puffin specifies the Alpha family, which is more accurate standalone:

```csharp
var sketch = UpdateThetaSketch.Builder()
    .SetFamily(SketchFamily.Alpha)   // requires >= 512 nominal entries
    .SetNominalEntries(4096)
    .Build();
```

### HLL

When all you need is a distinct count, HLL is markedly more compact than Theta
for the same accuracy — Theta earns its extra space by supporting intersection
and set difference, which HLL cannot do.

```csharp
using Clast.Sketches.Hll;

var sketch = new HllSketch(lgConfigK: 12, TgtHllType.Hll4);
foreach (var value in values)
    sketch.Update(value);

Console.WriteLine($"{sketch.Estimate} ({sketch.GetLowerBound()}..{sketch.GetUpperBound()})");

byte[] blob = sketch.ToCompactByteArray();
var loaded = HllSketch.Deserialize(blob);
```

HLL sketches merge too, and because registers hold a maximum the result is
exactly the sketch you would have built over the union of the inputs — no error
accumulates across merges. Sketches with different `k` or different register
widths can be mixed freely:

```csharp
var union = new HllUnion(lgMaxK: 12);
foreach (var blob in blobs)
    union.Update(blob);

HllSketch merged = union.GetResult();
```

This is the DataSketches HLL — what Spark's `hll_sketch_agg` produces — not the
HyperLogLog++ of the Google paper, which is a different algorithm with a
different wire format.

## Target frameworks

- `netstandard2.0`
- `net8.0`
- `net10.0`

## Relationship to Apache DataSketches

This is an independent implementation, not an ASF project and not affiliated
with or endorsed by the Apache Software Foundation. Algorithms, constants, and
wire formats are ported from the Apache-2.0 licensed DataSketches sources; see
[NOTICE](NOTICE) for attribution.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
