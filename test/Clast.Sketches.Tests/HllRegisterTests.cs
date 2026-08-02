using Clast.Sketches.Hll;

namespace Clast.Sketches.Tests;

/// <summary>
/// Parity between the vectorized register merge and the portable one.
/// </summary>
/// <remarks>
/// The vector path is selected at run time from the hardware, so on any given
/// machine only one of them executes in the sketch code. These tests run both
/// over the same inputs and require identical output — otherwise a SIMD bug
/// would only show up on the subset of machines that take that path.
/// </remarks>
public class HllRegisterTests
{
    /// <summary>
    /// Lengths chosen to straddle the vector widths: below one vector, exactly
    /// one, one plus a tail, and several with an awkward remainder. Register
    /// arrays are powers of two, but the tail handling has to be right anyway.
    /// </summary>
    public static IEnumerable<object[]> Lengths()
    {
        foreach (int length in new[] { 0, 1, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 4096, 4097, 65_536 })
        {
            yield return [length];
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void VectorAndScalarMergesAgree(int length)
    {
        var rng = new Random(length);

        byte[] source = new byte[length];
        byte[] target = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // Register values are 6 bits, so stay in range rather than using
            // arbitrary bytes.
            source[i] = (byte)rng.Next(0, 64);
            target[i] = (byte)rng.Next(0, 64);
        }

        byte[] viaVector = (byte[])target.Clone();
        byte[] viaScalar = (byte[])target.Clone();

        HllRegisters.MaxInto(source, viaVector);
        HllRegisters.MaxIntoScalar(source, viaScalar);

        Assert.Equal(viaScalar, viaVector);

        // And both really are the element-wise maximum.
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(Math.Max(source[i], target[i]), viaVector[i]);
        }
    }

    [Fact]
    public void MergeCoversTheFullByteRange()
    {
        // Byte maximum must be unsigned. A signed comparison would rank 0x80..0xFF
        // below 0x00, which register values never reach — but the primitive
        // should still be correct over its whole domain.
        byte[] source = new byte[256];
        byte[] target = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            source[i] = (byte)i;
            target[i] = (byte)(255 - i);
        }

        byte[] viaVector = (byte[])target.Clone();
        HllRegisters.MaxInto(source, viaVector);

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(Math.Max(source[i], target[i]), viaVector[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void VectorAndScalarNibbleExpansionAgree(int packedLength)
    {
        var rng = new Random(packedLength);
        byte[] packed = new byte[packedLength];
        rng.NextBytes(packed);

        const int curMin = 7;
        byte[] viaVector = new byte[packedLength * 2];
        byte[] viaScalar = new byte[packedLength * 2];

        HllRegisters.ExpandNibbles(packed, curMin, viaVector);
        HllRegisters.ExpandNibblesScalar(packed, curMin, viaScalar);

        Assert.Equal(viaScalar, viaVector);

        // Register 2i is the low nibble of packed byte i, register 2i+1 the high.
        for (int i = 0; i < packedLength; i++)
        {
            Assert.Equal((packed[i] & 0x0F) + curMin, viaVector[i * 2]);
            Assert.Equal(((packed[i] >> 4) & 0x0F) + curMin, viaVector[(i * 2) + 1]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(40)]
    public void NibbleExpansionHandlesEveryCurMin(int curMin)
    {
        byte[] packed = new byte[64];
        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] = (byte)i;
        }

        byte[] viaVector = new byte[128];
        byte[] viaScalar = new byte[128];
        HllRegisters.ExpandNibbles(packed, curMin, viaVector);
        HllRegisters.ExpandNibblesScalar(packed, curMin, viaScalar);

        Assert.Equal(viaScalar, viaVector);
    }

    [Fact]
    public void NibbleExpansionRejectsAnUndersizedDestination()
    {
        Assert.Throws<ArgumentException>(() => HllRegisters.ExpandNibbles(new byte[16], 0, new byte[31]));
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(128, 64)]
    [InlineData(4096, 512)]
    [InlineData(65_536, 4096)]
    public void FoldingTakesTheMaximumOverEachGroup(int sourceLength, int targetLength)
    {
        var rng = new Random(sourceLength);
        byte[] source = new byte[sourceLength];
        for (int i = 0; i < sourceLength; i++)
        {
            source[i] = (byte)rng.Next(0, 64);
        }

        byte[] folded = new byte[targetLength];
        HllRegisters.MaxIntoFolded(source, folded);

        // Source register i belongs to target register i mod targetLength.
        byte[] expected = new byte[targetLength];
        for (int i = 0; i < sourceLength; i++)
        {
            int j = i % targetLength;
            if (source[i] > expected[j]) { expected[j] = source[i]; }
        }

        Assert.Equal(expected, folded);
    }

    [Fact]
    public void FoldingPreservesWhatTheTargetAlreadyHeld()
    {
        byte[] source = new byte[128];
        byte[] target = new byte[64];
        source[0] = 5;
        target[0] = 9;
        target[1] = 3;

        HllRegisters.MaxIntoFolded(source, target);

        Assert.Equal(9, target[0]);
        Assert.Equal(3, target[1]);
    }

    [Fact]
    public void FoldingRejectsAMisalignedTarget()
    {
        Assert.Throws<ArgumentException>(() => HllRegisters.MaxIntoFolded(new byte[100], new byte[64]));
        Assert.Throws<ArgumentException>(() => HllRegisters.MaxIntoFolded(new byte[64], []));
    }
}
