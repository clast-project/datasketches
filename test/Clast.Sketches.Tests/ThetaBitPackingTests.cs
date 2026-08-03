using Clast.Sketches;
using Clast.Sketches.Theta;

namespace Clast.Sketches.Tests;

/// <summary>
/// Parity between the accumulator-based bit packer and the general one.
/// </summary>
/// <remarks>
/// Only one of them runs for any given entry width, so a bug in either would
/// otherwise surface only for the widths that route to it. These drive both over
/// the same inputs at every width and require identical bytes — and identical
/// round trips, since a packer and unpacker that are wrong in matching ways
/// would still round-trip.
/// </remarks>
public class ThetaBitPackingTests
{
    /// <summary>Every width the format allows.</summary>
    public static IEnumerable<object[]> Widths()
    {
        for (int bits = 1; bits <= 63; bits++)
        {
            yield return [bits];
        }
    }

    /// <summary>Ascending hashes whose gaps all fit in <paramref name="bits"/> bits.</summary>
    private static long[] AscendingWithGapWidth(int bits, int count, int seed)
    {
        var rng = new Random(seed);
        var values = new long[count];
        long running = 0;

        // Draw gaps that use the full width, so the top bit of the entry is
        // exercised rather than left implicitly zero.
        long maxGap = bits >= 63 ? long.MaxValue : (1L << bits) - 1;
        for (int i = 0; i < count; i++)
        {
            long gap = (long)(rng.NextDouble() * maxGap);
            if (gap == 0) { gap = 1; }
            // Keep the running total positive; hashes are 63-bit.
            if (running > long.MaxValue - gap) { break; }
            running += gap;
            values[i] = running;
        }

        int filled = Array.IndexOf(values, 0L);
        if (filled < 0)
        {
            return values;
        }

        // Explicit copy rather than a range: net472 has no GetSubArray.
        var truncated = new long[filled];
        Array.Copy(values, truncated, filled);
        return truncated;
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void AccumulatorAndGeneralPackersAgree(int bits)
    {
        long[] hashes = AscendingWithGapWidth(bits, 300, bits);
        if (hashes.Length < 2) { return; }

        long[] deltas = new long[hashes.Length];
        long previous = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            deltas[i] = hashes[i] - previous;
            previous = hashes[i];
        }

        int byteCount = Bits.WholeBytesToHoldBits(bits * hashes.Length);
        byte[] viaAccumulator = new byte[byteCount + 8];
        byte[] viaGeneral = new byte[byteCount + 8];

        ThetaBitPacking.PackDeltas(hashes, bits, viaAccumulator, 0);
        ThetaBitPacking.PackGeneral(deltas, bits, viaGeneral, 0, deltas.Length);

        Assert.Equal(viaGeneral, viaAccumulator);
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void AccumulatorAndGeneralUnpackersAgree(int bits)
    {
        long[] hashes = AscendingWithGapWidth(bits, 300, bits + 1000);
        if (hashes.Length < 2) { return; }

        int byteCount = Bits.WholeBytesToHoldBits(bits * hashes.Length);
        byte[] packed = new byte[byteCount + 8];
        ThetaBitPacking.PackDeltas(hashes, bits, packed, 0);

        long[] viaAccumulator = new long[hashes.Length];
        long[] viaGeneral = new long[hashes.Length];
        ThetaBitPacking.Unpack(viaAccumulator, bits, packed, 0, hashes.Length);
        ThetaBitPacking.UnpackGeneral(viaGeneral, bits, packed, 0, hashes.Length);

        Assert.Equal(viaGeneral, viaAccumulator);
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void DeltasRoundTripAtEveryWidth(int bits)
    {
        long[] hashes = AscendingWithGapWidth(bits, 300, bits + 2000);
        if (hashes.Length < 2) { return; }

        int byteCount = Bits.WholeBytesToHoldBits(bits * hashes.Length);
        byte[] packed = new byte[byteCount + 8];
        ThetaBitPacking.PackDeltas(hashes, bits, packed, 0);

        long[] deltas = new long[hashes.Length];
        ThetaBitPacking.Unpack(deltas, bits, packed, 0, hashes.Length);

        long running = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            running += deltas[i];
            Assert.Equal(hashes[i], running);
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void PackingWritesExactlyTheExpectedByteCount(int bits)
    {
        long[] hashes = AscendingWithGapWidth(bits, 64, bits + 3000);
        if (hashes.Length < 2) { return; }

        int expected = Bits.WholeBytesToHoldBits(bits * hashes.Length);
        byte[] buffer = new byte[expected + 16];

        int written = ThetaBitPacking.PackDeltas(hashes, bits, buffer, 0);

        Assert.Equal(expected, written);
        // Nothing beyond the reported length may be touched.
        for (int i = expected; i < buffer.Length; i++)
        {
            Assert.Equal(0, buffer[i]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(47)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(63)]
    public void PackingHonoursAStartingOffset(int bits)
    {
        long[] hashes = AscendingWithGapWidth(bits, 100, bits + 4000);
        if (hashes.Length < 2) { return; }

        const int offset = 13;
        int byteCount = Bits.WholeBytesToHoldBits(bits * hashes.Length);
        byte[] buffer = new byte[offset + byteCount + 8];

        ThetaBitPacking.PackDeltas(hashes, bits, buffer, offset);

        long[] deltas = new long[hashes.Length];
        ThetaBitPacking.Unpack(deltas, bits, buffer, offset, hashes.Length);

        long running = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            running += deltas[i];
            Assert.Equal(hashes[i], running);
        }

        // The bytes before the offset must be untouched.
        for (int i = 0; i < offset; i++)
        {
            Assert.Equal(0, buffer[i]);
        }
    }
}
