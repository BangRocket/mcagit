using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>The persistent chunk cache (#19). A corrupt cache must degrade gracefully (it only
/// accelerates), and Save must be atomic — a corrupt cache would otherwise break every subsequent
/// commit on a backup server.</summary>
public class ChunkCacheTests
{
    [Fact]
    public void SetSaveLoad_RoundTrips()
    {
        string dir = TestAnvil.TempDir("cc");
        ChunkCache c = ChunkCache.Load(dir);
        c.Set("payload-key", "object-hash");
        c.Save();

        ChunkCache reloaded = ChunkCache.Load(dir);
        Assert.True(reloaded.TryGet("payload-key", out string hash));
        Assert.Equal("object-hash", hash);
        Assert.False(reloaded.TryGet("absent", out _));
    }

    [Fact]
    public void CorruptCache_LoadsEmpty_DoesNotThrow()
    {
        string dir = TestAnvil.TempDir("ccx");
        File.WriteAllText(Path.Combine(dir, "chunkcache.json"), "{ this is not json ]]");

        ChunkCache c = ChunkCache.Load(dir);          // must not throw
        Assert.False(c.TryGet("anything", out _));    // starts empty

        c.Set("k", "v");                              // and is still usable afterwards
        c.Save();
        Assert.True(ChunkCache.Load(dir).TryGet("k", out _));
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        string dir = TestAnvil.TempDir("cct");
        ChunkCache c = ChunkCache.Load(dir);
        c.Set("a", "1");
        c.Save();
        c.Set("b", "2");
        c.Save();

        Assert.True(File.Exists(Path.Combine(dir, "chunkcache.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp")); // atomic temp-then-move leaves nothing behind
    }
}
