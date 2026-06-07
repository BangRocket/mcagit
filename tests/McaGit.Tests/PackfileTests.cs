using System.Security.Cryptography;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>
/// Packfile writing: the serial <see cref="Packfile.Write"/> and the parallel
/// <see cref="Packfile.WriteParallel"/> must produce packs whose every object reads back
/// byte-identical, share the same (set-based) pack id, and preserve within-segment delta chains.
/// </summary>
public class PackfileTests
{
    // N objects with controlled content: blobs share a prefix within size-bands so the delta window
    // fires, with per-object bytes making each unique. Returns hashes ordered by size desc (as Gc
    // orders), a content map, and a size lookup.
    private static (List<string> Ordered, Dictionary<string, byte[]> ByHash) MakeObjects(int n)
    {
        var byHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            int band = i % 5;
            var content = new byte[256 + band * 64 + (i % 17)];
            for (int j = 0; j < content.Length; j++) content[j] = (byte)((band * 31 + j) & 0xFF);
            content[0] = (byte)i; content[1] = (byte)(i >> 8);   // make each object unique
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            byHash[hash] = content;
            sizes[hash] = content.Length;
        }
        var ordered = byHash.Keys
            .OrderByDescending(h => sizes[h])
            .ThenBy(h => h, StringComparer.Ordinal)
            .ToList();
        return (ordered, byHash);
    }

    private static void AssertPackRoundTrips(string objectsDir, string id, Dictionary<string, byte[]> byHash)
    {
        string packPath = Path.Combine(objectsDir, "pack", $"pack-{id}.pack");
        using Packfile pf = Packfile.Open(packPath);
        Assert.Equal(byHash.Count, pf.Hashes.Count);
        foreach ((string hash, byte[] want) in byHash)
            Assert.Equal(want, pf.Read(hash));
    }

    [Fact]
    public void Write_RoundTripsEveryObject()
    {
        (List<string> ordered, Dictionary<string, byte[]> byHash) = MakeObjects(200);
        string dir = TestAnvil.TempDir("pf-write");
        string? id = Packfile.Write(dir, ordered, h => byHash[h]);
        Assert.NotNull(id);
        AssertPackRoundTrips(dir, id!, byHash);
    }
}
