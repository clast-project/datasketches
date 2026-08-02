using System.Text;

namespace Clast.Sketches.Tests;

/// <summary>
/// Expected values are taken from <c>MurmurHash3Test</c> in apache/datasketches-java,
/// so a pass here means serialized sketches hash-match the reference implementations.
/// </summary>
public class MurmurHash3Tests
{
    [Theory]
    // Remainder > 8 bytes (43 bytes).
    [InlineData("The quick brown fox jumps over the lazy dog", 0xe34bbc7bbc071b6cUL, 0x7a433ca9c49a9347UL)]
    // Same length, one bit different — checks avalanche, not just arithmetic.
    [InlineData("The quick brown fox jumps over the lazy eog", 0x362108102c62d1c9UL, 0x3285cd100292b305UL)]
    // Remainder < 8 bytes (48 bytes).
    [InlineData("The quick brown fox jumps over the lazy dogdogdog", 0x9c8205300e612fc4UL, 0xcbc0af6136aa3df9UL)]
    // Remainder exactly 8 bytes (40 bytes).
    [InlineData("The quick brown fox jumps over the lazy1", 0xe3301a827e5cdfe3UL, 0xbdbf05f8da0f0392UL)]
    public void MatchesJavaReference_ForUtf8Strings(string key, ulong expectedH1, ulong expectedH2)
    {
        var hash = MurmurHash3.Hash(Encoding.UTF8.GetBytes(key), seed: 0);

        Assert.Equal(expectedH1, hash.H1);
        Assert.Equal(expectedH2, hash.H2);
    }

    [Fact]
    public void MatchesJavaReference_ForBytesWithAllOnesAndAllZeros()
    {
        byte[] key =
        [
            0x54, 0x68, 0x65, 0x20, 0x71, 0x75, 0x69, 0x63, 0x6b, 0x20, 0x62, 0x72, 0x6f, 0x77, 0x6e,
            0x20, 0x66, 0x6f, 0x78, 0x20, 0x6a, 0x75, 0x6d, 0x70, 0x73, 0x20, 0x6f, 0x76, 0x65,
            0x72, 0x20, 0x74, 0x68, 0x65, 0x20, 0x6c, 0x61, 0x7a, 0x79, 0x20, 0x64, 0x6f, 0x67,
            0xff, 0x64, 0x6f, 0x67, 0x00,
        ];

        var hash = MurmurHash3.Hash(key, seed: 0);

        Assert.Equal(0xe88abda785929c9eUL, hash.H1);
        Assert.Equal(0x96b98587cacc83d6UL, hash.H2);
    }

    [Fact]
    public void LongSpanOverload_AgreesWithByteOverload()
    {
        // 40 bytes = exactly 5 longs, so no partial-long padding is involved.
        byte[] bytes = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy1");
        Assert.Equal(40, bytes.Length);

        long[] longs = new long[bytes.Length / sizeof(long)];
        for (int i = 0; i < longs.Length; i++)
        {
            longs[i] = BitConverter.ToInt64(bytes, i * sizeof(long));
        }

        var fromLongs = MurmurHash3.Hash(longs.AsSpan(), seed: 0);

        Assert.Equal(0xe3301a827e5cdfe3UL, fromLongs.H1);
        Assert.Equal(0xbdbf05f8da0f0392UL, fromLongs.H2);
        Assert.Equal(MurmurHash3.Hash(bytes, seed: 0), fromLongs);
    }

    [Fact]
    public void ScalarLongOverload_AgreesWithItsLittleEndianBytes()
    {
        foreach (long value in new[] { 0L, 1L, -1L, long.MinValue, long.MaxValue, 9001L, 0x0123456789abcdefL })
        {
            var fromScalar = MurmurHash3.Hash(value, seed: 9001);
            var fromBytes = MurmurHash3.Hash(BitConverter.GetBytes(value), seed: 9001);
            var fromSpan = MurmurHash3.Hash(new[] { value }.AsSpan(), seed: 9001);

            Assert.Equal(fromBytes, fromScalar);
            Assert.Equal(fromBytes, fromSpan);
        }
    }

    [Fact]
    public void SeedChangesTheHash()
    {
        byte[] key = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");

        Assert.NotEqual(MurmurHash3.Hash(key, seed: 0), MurmurHash3.Hash(key, seed: 9001));
    }

    [Fact]
    public void AllTailLengths_AgreeAcrossBlockBoundaries()
    {
        // Walks every tail length 0..15 twice over, so each branch of the
        // partial-long assembly is exercised.
        byte[] data = new byte[64];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7);
        }

        var seen = new HashSet<Hash128>();
        for (int len = 0; len <= data.Length; len++)
        {
            var hash = MurmurHash3.Hash(data.AsSpan(0, len), seed: 9001);
            Assert.True(seen.Add(hash), $"Duplicate hash at length {len}");
        }
    }
}
