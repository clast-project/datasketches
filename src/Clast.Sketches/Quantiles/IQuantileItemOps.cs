// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Quantiles;

/// <summary>
/// The element-type operations a quantiles sketch needs: ordering, and the
/// little-endian wire encoding of a single item.
/// </summary>
/// <remarks>
/// <para>
/// The quantiles algorithms are identical for every element type — only the
/// comparison and the item width differ — so the engine is written once and
/// parameterized by this interface. Implementations are structs used as generic
/// type arguments, which the JIT specializes per instantiation, so the calls
/// devirtualize and there is no boxing.
/// </para>
/// <para>
/// The reference library takes the other road and duplicates the whole
/// implementation per type (<c>KllDoublesSketch</c>, <c>KllFloatsSketch</c>,
/// <c>KllLongsSketch</c>). Structuring it this way keeps one copy of the
/// compaction and merge logic, which is where the subtle parts live.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type stored by the sketch.</typeparam>
internal interface IQuantileItemOps<T>
{
    /// <summary>The serialized width of one item, in bytes.</summary>
    int SizeBytes { get; }

    /// <summary>True if <paramref name="a"/> sorts strictly before <paramref name="b"/>.</summary>
    bool LessThan(T a, T b);

    /// <summary>Reads one item from the start of <paramref name="source"/>.</summary>
    T Read(ReadOnlySpan<byte> source);

    /// <summary>Writes one item to the start of <paramref name="destination"/>.</summary>
    void Write(Span<byte> destination, T value);

    /// <summary>Sorts <paramref name="count"/> items of <paramref name="array"/> from <paramref name="index"/>.</summary>
    void Sort(T[] array, int index, int count);
}

/// <summary>Double-precision items.</summary>
internal readonly struct DoubleOps : IQuantileItemOps<double>
{
    public int SizeBytes => sizeof(double);

    public bool LessThan(double a, double b) => a < b;

    public double Read(ReadOnlySpan<byte> source) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source));

    public void Write(Span<byte> destination, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(value));

    public void Sort(double[] array, int index, int count) => Array.Sort(array, index, count);
}

/// <summary>Single-precision items.</summary>
internal readonly struct FloatOps : IQuantileItemOps<float>
{
    public int SizeBytes => sizeof(float);

    public bool LessThan(float a, float b) => a < b;

    public float Read(ReadOnlySpan<byte> source) =>
        Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));

    public void Write(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, SingleToInt32Bits(value));

    public void Sort(float[] array, int index, int count) => Array.Sort(array, index, count);

    // BitConverter.SingleToInt32Bits does not exist on netstandard2.0.
    private static unsafe int SingleToInt32Bits(float value) => *(int*)&value;

    private static unsafe float Int32BitsToSingle(int value) => *(float*)&value;
}

/// <summary>64-bit integer items.</summary>
internal readonly struct LongOps : IQuantileItemOps<long>
{
    public int SizeBytes => sizeof(long);

    public bool LessThan(long a, long b) => a < b;

    public long Read(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt64LittleEndian(source);

    public void Write(Span<byte> destination, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);

    public void Sort(long[] array, int index, int count) => Array.Sort(array, index, count);
}
