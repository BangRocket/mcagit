using System.Security.Cryptography;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Batched pack-on-the-wire transfer (#15): a multi-object push becomes one pack.
/// A received pack is untrusted input, so every object is hash-verified on ingest.</summary>
public class PackTransferTests
{
    private static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void Build_Then_ImportInto_RoundTrips_AndVerifies()
    {
        byte[] a = "the quick brown fox"u8.ToArray();
        byte[] b = "the quick brown fox jumps over the lazy dog"u8.ToArray(); // similar → exercises delta packing
        (byte[] Pack, byte[] Idx)? built = PackTransfer.Build([(Hash(a), a), (Hash(b), b)]);
        Assert.NotNull(built);

        Repository repo = Repository.Init(TestAnvil.TempDir("ptr"));
        int n = PackTransfer.ImportInto(repo, built.Value.Pack, built.Value.Idx);

        Assert.Equal(2, n);
        Assert.True(repo.Objects.Exists(Hash(a)) && repo.Objects.Exists(Hash(b)));
        Assert.True(repo.Objects.VerifyIntegrity(Hash(a)));
        Assert.Equal(a, repo.Objects.Read(Hash(a)));
    }

    [Fact]
    public void ImportInto_RejectsTamperedPack()
    {
        byte[] a = "real content"u8.ToArray();
        byte[] forged = "forged content"u8.ToArray();
        // Claim a's hash for forged content — a hostile remote's poison attempt.
        (byte[] Pack, byte[] Idx)? bad = PackTransfer.Build([(Hash(a), forged)]);
        Assert.NotNull(bad);

        Repository repo = Repository.Init(TestAnvil.TempDir("ptt"));
        Assert.ThrowsAny<Exception>(() => PackTransfer.ImportInto(repo, bad.Value.Pack, bad.Value.Idx));
        Assert.False(repo.Objects.Exists(Hash(a))); // nothing poisoned
    }

    [Fact]
    public void ImportInto_RejectsOutOfRangeOffset_NoCrash()
    {
        // A hostile pack whose index points an object past EOF must be rejected cleanly
        // (InvalidDataException), never an unhandled crash / OOB read.
        byte[] a = "content"u8.ToArray();
        (byte[] Pack, byte[] Idx)? built = PackTransfer.Build([(Hash(a), a)]);
        byte[] idx = built!.Value.Idx;
        // idx = [MCAI(4)][ver(1)][count(4)] then 40-byte entries (32 hash + 8 BE offset).
        // Corrupt the first entry's offset (bytes 41..49) to point far past the pack.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(idx.AsSpan(9 + 32, 8), long.MaxValue / 2);

        Repository repo = Repository.Init(TestAnvil.TempDir("pto"));
        Assert.Throws<InvalidDataException>(() => PackTransfer.ImportInto(repo, built.Value.Pack, idx));
        Assert.False(repo.Objects.Exists(Hash(a)));
    }

    [Fact]
    public void FrameBody_RoundTrips()
    {
        byte[] pack = [1, 2, 3, 4, 5];
        byte[] idx = [9, 8, 7];
        (byte[] Pack, byte[] Idx) back = PackTransfer.UnframeBody(PackTransfer.FrameBody(pack, idx));
        Assert.Equal(pack, back.Pack);
        Assert.Equal(idx, back.Idx);
    }
}
