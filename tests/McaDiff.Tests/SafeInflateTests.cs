using System.IO.Compression;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>The output-bounded decompression guard (#21): a tiny input that inflates past the cap is
/// rejected with a catchable InvalidDataException, never an OOM.</summary>
public class SafeInflateTests
{
    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Zlib_Bomb_ThrowsInsteadOfOOM()
    {
        byte[] big = new byte[4_000_000];          // 4 MB of zeros → a few KB compressed
        byte[] comp = Zlib(big);
        Assert.True(comp.Length < big.Length / 10); // confirm it's a "bomb" shape

        Assert.Throws<InvalidDataException>(() => SafeInflate.Zlib(comp, max: 1_000_000)); // cap below output
        Assert.Equal(big, SafeInflate.Zlib(comp, max: 8_000_000));                         // within cap → round-trips
    }

    [Fact]
    public void ReadBounded_CapsAStream()
    {
        using var s = new MemoryStream(new byte[2000]);
        Assert.Throws<InvalidDataException>(() => SafeInflate.ReadBounded(s, max: 1000));
    }
}
