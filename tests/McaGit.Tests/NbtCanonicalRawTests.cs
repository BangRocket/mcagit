using fNbt;
using McaGit.Anvil;
using McaGit.Nbt;
using Xunit;

namespace McaGit.Tests;

/// <summary>
/// Pins the allocation-light <see cref="NbtCanonical.CanonicalizeRaw"/> to the fNbt-tree path
/// (<see cref="NbtCanonical.Serialize"/>): for every input the two MUST produce byte-identical canonical
/// output, or the commit hash would diverge from existing repos and break dedup/round-trip. Covers every
/// tag type, nested/unsorted compounds, lists (incl. of compounds/lists), empties, unicode names, a
/// seeded fuzzer, and the real sample worlds.
/// </summary>
public class NbtCanonicalRawTests
{
    private static byte[] RawOf(NbtCompound root) =>
        new NbtFile(root) { BigEndian = true }.SaveToBuffer(NbtCompression.None);

    private static void AssertMatches(NbtCompound root)
    {
        byte[] raw = RawOf(root);
        byte[] viaTree = NbtCanonical.Serialize(root);          // parse-free here: Serialize sorts a tree
        byte[] viaRaw = NbtCanonical.CanonicalizeRaw(raw);       // sorts the bytes directly
        Assert.Equal(viaTree, viaRaw);
    }

    [Fact]
    public void Matches_AcrossEveryTagShape()
    {
        foreach (NbtCompound root in SampleRoots()) AssertMatches(root);
    }

    [Fact]
    public void Matches_DeterministicFuzz()
    {
        var r = new Random(20260606);
        for (int i = 0; i < 400; i++)
        {
            var root = new NbtCompound("");
            int n = r.Next(0, 8);
            for (int k = 0; k < n; k++) root.Add(RandTag(r, $"k{r.Next(0, 50)}_{k}", 0));
            AssertMatches(root);
        }
    }

    [Fact]
    public void Matches_RealSampleWorldChunks()
    {
        // The repo ships two real sample worlds; walk every chunk of every region and compare.
        string baseDir = FindRepoRoot();
        int chec_d = 0;
        foreach (string world in new[] { "New_World_Older", "New_World_Newer" })
        {
            string regionDir = Path.Combine(baseDir, "compare-worlds", world, "region");
            if (!Directory.Exists(regionDir)) continue;
            foreach (string mca in Directory.EnumerateFiles(regionDir, "*.mca"))
                foreach (RawChunk rc in RegionFile.Open(mca).Chunks)
                {
                    NbtCompound root = ChunkCodec.Decode(rc);
                    byte[] viaTree = NbtCanonical.Serialize(root);
                    byte[] viaRaw = NbtCanonical.CanonicalizeRaw(RawOf(root));
                    Assert.Equal(viaTree, viaRaw);
                    chec_d++;
                }
        }
        // Don't silently pass if the fixtures moved — at least assert we exercised the path when present.
        if (Directory.Exists(Path.Combine(baseDir, "compare-worlds"))) Assert.True(chec_d > 0, "no sample chunks were checked");
    }

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "McaGit.sln"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    // ---- sample roots: hand-picked to exercise sorting + every payload kind + edge cases ----

    private static IEnumerable<NbtCompound> SampleRoots()
    {
        yield return new NbtCompound("");                                       // empty root
        yield return new NbtCompound("named");                                  // empty, non-empty root name

        // keys deliberately out of order, at multiple levels, to force the sort
        yield return new NbtCompound("") {
            new NbtString("zeta", "z"), new NbtInt("alpha", 1), new NbtByte("mid", 7),
            new NbtCompound("nested") { new NbtInt("y", 2), new NbtInt("x", 1), new NbtInt("a", 0) },
        };

        // every scalar, with tricky values (negatives, zero, NaN/Inf, large)
        yield return new NbtCompound("") {
            new NbtByte("b", 0), new NbtByte("bneg", unchecked((byte)-1)),
            new NbtShort("s", short.MinValue), new NbtInt("i", int.MinValue), new NbtLong("l", long.MaxValue),
            new NbtFloat("f", float.NaN), new NbtFloat("f2", -0.0f), new NbtFloat("finf", float.PositiveInfinity),
            new NbtDouble("d", double.NaN), new NbtDouble("d2", -0.0), new NbtDouble("dinf", double.NegativeInfinity),
            new NbtDouble("dpi", 3.141592653589793),
        };

        // strings: empty, ascii, spaces, unicode, embedded special chars
        yield return new NbtCompound("") {
            new NbtString("empty", ""), new NbtString("ascii", "hello"), new NbtString("space", "a b c"),
            new NbtString("unicode", "héllo-世界-🧱"), new NbtString("emoji", "🟩"),
        };

        // arrays: empty + non-empty
        yield return new NbtCompound("") {
            new NbtByteArray("ba", []), new NbtByteArray("ba2", [1, 2, 3, 255]),
            new NbtIntArray("ia", []), new NbtIntArray("ia2", [int.MinValue, 0, int.MaxValue]),
            new NbtLongArray("la", []), new NbtLongArray("la2", [long.MinValue, 0, long.MaxValue]),
        };

        // lists: empty, scalars, of compounds (each with unsorted keys), of lists (nested)
        var listOfCompounds = new NbtList("lc", NbtTagType.Compound) {
            new NbtCompound { new NbtInt("c", 3), new NbtInt("a", 1) },
            new NbtCompound { new NbtInt("z", 9), new NbtInt("b", 2) },
        };
        var listOfLists = new NbtList("ll", NbtTagType.List) {
            new NbtList(NbtTagType.Int) { new NbtInt(1), new NbtInt(2) },
            new NbtList(NbtTagType.Int) { new NbtInt(3) },
        };
        yield return new NbtCompound("") {
            new NbtList("empty", NbtTagType.Int),
            new NbtList("ints", NbtTagType.Int) { new NbtInt(3), new NbtInt(1), new NbtInt(2) }, // order preserved
            new NbtList("strs", NbtTagType.String) { new NbtString("b"), new NbtString("a") },   // order preserved
            listOfCompounds, listOfLists,
        };

        // many keys in reverse order (stress the sort)
        var reverse = new NbtCompound("");
        for (int i = 30; i >= 0; i--) reverse.Add(new NbtInt($"key{i:D2}", i));
        yield return reverse;
    }

    private static NbtTag RandTag(Random r, string? name, int depth)
    {
        int kind = depth >= 4 ? r.Next(0, 9) : r.Next(0, 12); // cap nesting so fuzz stays cheap
        return kind switch
        {
            0 => new NbtByte(name, (byte)r.Next(256)),
            1 => new NbtShort(name, (short)r.Next(short.MinValue, short.MaxValue)),
            2 => new NbtInt(name, r.Next()),
            3 => new NbtLong(name, ((long)r.Next() << 32) | (uint)r.Next()),
            4 => new NbtFloat(name, (float)(r.NextDouble() * 1e6 - 5e5)),
            5 => new NbtDouble(name, r.NextDouble() * 1e9 - 5e8),
            6 => new NbtString(name, RandStr(r)),
            7 => MakeByteArray(name, r),
            8 => MakeIntArray(name, r),
            9 => MakeList(r, name, depth),
            10 => MakeCompound(r, name, depth),
            _ => MakeLongArray(name, r),
        };
    }

    private static NbtTag MakeByteArray(string? name, Random r)
    {
        var a = new byte[r.Next(0, 6)];
        r.NextBytes(a);
        return name is null ? new NbtByteArray(a) : new NbtByteArray(name, a);
    }

    private static NbtTag MakeIntArray(string? name, Random r)
    {
        var a = new int[r.Next(0, 6)];
        for (int i = 0; i < a.Length; i++) a[i] = r.Next();
        return name is null ? new NbtIntArray(a) : new NbtIntArray(name, a);
    }

    private static NbtTag MakeLongArray(string? name, Random r)
    {
        var a = new long[r.Next(0, 6)];
        for (int i = 0; i < a.Length; i++) a[i] = (long)r.Next() << 20;
        return name is null ? new NbtLongArray(a) : new NbtLongArray(name, a);
    }

    private static NbtTag MakeCompound(Random r, string? name, int depth)
    {
        NbtCompound c = name is null ? new NbtCompound() : new NbtCompound(name);
        int n = r.Next(0, 6);
        var used = new HashSet<string>();
        for (int i = 0; i < n; i++)
        {
            string key = $"f{r.Next(0, 40)}_{i}";
            if (used.Add(key)) c.Add(RandTag(r, key, depth + 1));
        }
        return c;
    }

    private static NbtTag MakeList(Random r, string? name, int depth)
    {
        // fNbt lists are homogeneous and hold unnamed tags; pick a simple element type.
        NbtTagType et = (NbtTagType)(new[] { NbtTagType.Int, NbtTagType.String, NbtTagType.Compound, NbtTagType.Double }[r.Next(4)]);
        NbtList list = name is null ? new NbtList(et) : new NbtList(name, et);
        int n = r.Next(0, 5);
        for (int i = 0; i < n; i++)
            list.Add(et switch
            {
                NbtTagType.Int => new NbtInt(r.Next()),
                NbtTagType.String => new NbtString(RandStr(r)),
                NbtTagType.Double => new NbtDouble(r.NextDouble()),
                _ => (NbtTag)MakeCompound(r, null, depth + 1),
            });
        return list;
    }

    private static string RandStr(Random r)
    {
        string[] pool = ["", "a", "abc", "x y", "héllo", "世界", "🧱", "minecraft:stone", "Name_01"];
        return pool[r.Next(pool.Length)];
    }
}
