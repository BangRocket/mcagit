using fNbt;
using McaGit.Anvil;
using McaGit.Diff;

namespace McaGit.Query;

/// <summary>
/// Read-only semantic queries over a world's parsed NBT — "what *is* the world", as opposed to the
/// diff engine's "what *changed*". Reuses <see cref="ChunkCodec"/> for chunk decode and
/// <see cref="BlockStateDecoder"/> for block lookup. Never modifies the world.
/// </summary>
public sealed class WorldQuery
{
    private readonly string _worldRoot;
    private readonly string _dimRoot; // world root + dimension sub-dir

    public WorldQuery(string worldDir, string dimension = "")
    {
        _worldRoot = Path.GetFullPath(worldDir);
        string sub = Dimensions.SubDir(dimension);
        _dimRoot = sub.Length == 0 ? _worldRoot : Path.Combine(_worldRoot, sub);
    }

    /// <summary>Block entities (chests/signs/spawners/…). <paramref name="idFilter"/> matches the
    /// namespaced id (substring, case-insensitive); <paramref name="near"/>/<paramref name="radius"/>
    /// limit to a cube around a point.</summary>
    public IEnumerable<BlockEntityHit> BlockEntities(string? idFilter = null,
        (int X, int Y, int Z)? near = null, int radius = 64)
    {
        foreach ((NbtCompound root, string region) in Chunks("region"))
        {
            NbtList? list = root.Get<NbtList>("block_entities")                          // 1.18+
                ?? root.Get<NbtCompound>("Level")?.Get<NbtList>("TileEntities");          // legacy
            if (list is null) continue;
            foreach (NbtTag t in list)
            {
                if (t is not NbtCompound be) continue;
                string id = be.Get<NbtString>("id")?.Value ?? "?";
                int x = be.Get<NbtInt>("x")?.Value ?? 0, y = be.Get<NbtInt>("y")?.Value ?? 0, z = be.Get<NbtInt>("z")?.Value ?? 0;
                if (!Matches(id, idFilter) || !InRange(x, y, z, near, radius)) continue;
                int items = (be.Get<NbtList>("Items")?.Count) ?? 0;
                yield return new BlockEntityHit(id, x, y, z, region, items);
            }
        }
    }

    /// <summary>Entities (mobs / frames / armour stands / …) from the <c>entities/</c> region files
    /// (1.17+), falling back to legacy <c>Level.Entities</c> in terrain chunks.</summary>
    public IEnumerable<EntityHit> Entities(string? idFilter = null,
        (int X, int Y, int Z)? near = null, int radius = 64)
    {
        var sources = new List<(string Cat, Func<NbtCompound, NbtList?> Get)>
        {
            ("entities", r => r.Get<NbtList>("Entities")),                                // 1.17+ split file
            ("region", r => r.Get<NbtCompound>("Level")?.Get<NbtList>("Entities")),        // pre-1.17 in terrain
        };
        foreach ((string cat, Func<NbtCompound, NbtList?> get) in sources)
            foreach ((NbtCompound root, string region) in Chunks(cat))
            {
                if (get(root) is not { } list) continue;
                foreach (NbtTag t in list)
                {
                    if (t is not NbtCompound e || e.Get<NbtList>("Pos") is not { Count: 3 } pos) continue;
                    string id = e.Get<NbtString>("id")?.Value ?? "?";
                    double px = D(pos[0]), py = D(pos[1]), pz = D(pos[2]);
                    if (!Matches(id, idFilter) || !InRange((int)px, (int)py, (int)pz, near, radius)) continue;
                    yield return new EntityHit(id, px, py, pz, CustomName(e), region);
                }
            }
    }

    /// <summary>Players' last-saved state: single-player (<c>level.dat</c> → <c>Data.Player</c>) plus
    /// every <c>playerdata/&lt;uuid&gt;.dat</c>.</summary>
    public IEnumerable<PlayerHit> Players()
    {
        string levelDat = Path.Combine(_worldRoot, "level.dat");
        if (File.Exists(levelDat) && TryLoad(levelDat) is { } lvl
            && lvl.Get<NbtCompound>("Data")?.Get<NbtCompound>("Player") is { } sp)
            yield return ToPlayer("singleplayer", sp);

        string playerDir = Path.Combine(_worldRoot, "playerdata");
        if (Directory.Exists(playerDir))
            foreach (string f in Directory.EnumerateFiles(playerDir, "*.dat"))
                if (TryLoad(f) is { } p)
                    yield return ToPlayer(Path.GetFileNameWithoutExtension(f), p);
    }

    /// <summary>Points of interest (beds / workstations / portals) from the <c>poi/</c> region files.</summary>
    public IEnumerable<PoiHit> Poi(string? typeFilter = null, (int X, int Y, int Z)? near = null, int radius = 64)
    {
        foreach ((NbtCompound root, string region) in Chunks("poi"))
        {
            if (root.Get<NbtCompound>("Sections") is not { } sections) continue;
            foreach (NbtTag secTag in sections)
            {
                if (secTag is not NbtCompound sec || sec.Get<NbtList>("Records") is not { } records) continue;
                foreach (NbtTag rt in records)
                {
                    if (rt is not NbtCompound rec || rec.Get<NbtIntArray>("pos")?.Value is not { Length: 3 } p) continue;
                    string type = rec.Get<NbtString>("type")?.Value ?? "?";
                    if (!Matches(type, typeFilter) || !InRange(p[0], p[1], p[2], near, radius)) continue;
                    yield return new PoiHit(type, p[0], p[1], p[2], region);
                }
            }
        }
    }

    /// <summary>Signs whose text contains <paramref name="pattern"/> (case-insensitive), across the
    /// 1.20+ <c>front_text/back_text.messages</c> and legacy <c>Text1..4</c> formats.</summary>
    public IEnumerable<SignHit> Signs(string? pattern = null, (int X, int Y, int Z)? near = null, int radius = 64)
    {
        foreach ((NbtCompound root, string region) in Chunks("region"))
        {
            NbtList? list = root.Get<NbtList>("block_entities") ?? root.Get<NbtCompound>("Level")?.Get<NbtList>("TileEntities");
            if (list is null) continue;
            foreach (NbtTag t in list)
            {
                if (t is not NbtCompound be || be.Get<NbtString>("id")?.Value is not { } id || !id.Contains("sign", StringComparison.OrdinalIgnoreCase)) continue;
                int x = be.Get<NbtInt>("x")?.Value ?? 0, y = be.Get<NbtInt>("y")?.Value ?? 0, z = be.Get<NbtInt>("z")?.Value ?? 0;
                if (!InRange(x, y, z, near, radius)) continue;
                List<string> lines = SignLines(be);
                if (lines.Count == 0) continue;
                if (pattern is not null && !lines.Any(l => l.Contains(pattern, StringComparison.OrdinalIgnoreCase))) continue;
                yield return new SignHit(x, y, z, lines, region);
            }
        }
    }

    /// <summary>The block (and biome) at an absolute coordinate, or null if the chunk/section isn't present.</summary>
    public BlockInspect? BlockAt(int x, int y, int z)
    {
        int chunkX = x >> 4, chunkZ = z >> 4;
        string regionPath = Path.Combine(_dimRoot, "region", $"r.{chunkX >> 5}.{chunkZ >> 5}.mca");
        if (!File.Exists(regionPath)) return null;
        if (!RegionFile.Open(regionPath).TryGet(new ChunkPos(chunkX, chunkZ), out RawChunk rc)) return null;

        NbtCompound root = ChunkCodec.Decode(rc);
        if (root.Get<NbtList>("sections") is not { } sections) return null;
        sbyte secY = (sbyte)(y >> 4);
        foreach (NbtTag t in sections)
        {
            if (t is not NbtCompound sec || sec.Get<NbtByte>("Y") is not { } yb || unchecked((sbyte)yb.Value) != secY) continue;
            int lx = x & 15, ly = y & 15, lz = z & 15;
            string block = sec.Get<NbtCompound>("block_states") is { } bs
                && BlockStateDecoder.Decode(bs, 4096, BlockStateDecoder.BlockMinBits) is { } grid
                ? grid[(ly * 16 + lz) * 16 + lx] : "minecraft:air";
            string? biome = sec.Get<NbtCompound>("biomes") is { } bm
                && BlockStateDecoder.Decode(bm, 64, BlockStateDecoder.BiomeMinBits) is { } bgrid
                ? bgrid[((ly >> 2) * 4 + (lz >> 2)) * 4 + (lx >> 2)] : null;
            return new BlockInspect(x, y, z, block, biome, Path.GetRelativePath(_worldRoot, _dimRoot) is "." ? "overworld" : Path.GetFileName(_dimRoot));
        }
        return null;
    }

    // ---- internals ----

    private IEnumerable<(NbtCompound Root, string Region)> Chunks(string category)
    {
        string dir = Path.Combine(_dimRoot, category);
        if (!Directory.Exists(dir)) yield break;
        foreach (string file in Directory.EnumerateFiles(dir, "*.mca"))
        {
            string rel = Path.GetRelativePath(_worldRoot, file).Replace('\\', '/');
            RegionFile region;
            try { region = RegionFile.Open(file); } catch { continue; }
            foreach (RawChunk rc in region.Chunks)
            {
                NbtCompound root;
                try { root = ChunkCodec.Decode(rc); } catch { continue; } // skip an undecodable (e.g. type-127) chunk
                yield return (root, rel);
            }
        }
    }

    private static bool Matches(string id, string? filter) =>
        filter is null || id.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool InRange(int x, int y, int z, (int X, int Y, int Z)? near, int radius) =>
        near is not { } n || (Math.Abs(x - n.X) <= radius && Math.Abs(y - n.Y) <= radius && Math.Abs(z - n.Z) <= radius);

    private static double D(NbtTag t) => t is NbtDouble d ? d.Value : t is NbtFloat f ? f.Value : 0;

    private static string? CustomName(NbtCompound e) =>
        e.Get<NbtString>("CustomName")?.Value is { Length: > 0 } n ? n : null;

    private static NbtCompound? TryLoad(string path)
    {
        try { return ChunkCodec.LoadNbtFile(path); } catch { return null; }
    }

    private static PlayerHit ToPlayer(string source, NbtCompound p)
    {
        double x = 0, y = 0, z = 0;
        if (p["Pos"] is NbtList { Count: 3 } pos) { x = D(pos[0]); y = D(pos[1]); z = D(pos[2]); }
        // Dimension was a TAG_Int (0/-1/1) pre-1.16, a TAG_String after — use the non-throwing indexer.
        string dim = p["Dimension"] switch
        {
            NbtString s => s.Value,
            NbtInt { Value: -1 } => "minecraft:the_nether",
            NbtInt { Value: 1 } => "minecraft:the_end",
            _ => "minecraft:overworld",
        };
        double health = p["Health"] is NbtFloat h ? h.Value : -1;
        int gm = p["playerGameType"] is NbtInt g ? g.Value : -1;
        return new PlayerHit(source, x, y, z, dim, health, gm);
    }

    private static List<string> SignLines(NbtCompound sign)
    {
        var lines = new List<string>();
        foreach (string side in new[] { "front_text", "back_text" }) // 1.20+ (DV 3709)
            if (sign.Get<NbtCompound>(side)?.Get<NbtList>("messages") is { } msgs)
                foreach (NbtTag m in msgs)
                    if (m is NbtString s && PlainText(s.Value) is { Length: > 0 } txt) lines.Add(txt);
        for (int i = 1; i <= 4; i++) // legacy Text1..Text4
            if (sign.Get<NbtString>($"Text{i}")?.Value is { } legacy && PlainText(legacy) is { Length: > 0 } txt) lines.Add(txt);
        return lines;
    }

    /// <summary>Sign lines are JSON text components (<c>{"text":"…"}</c>) or plain strings — pull out
    /// the visible text best-effort, leaving anything unrecognized as-is.</summary>
    private static string PlainText(string raw)
    {
        string t = raw.Trim();
        if (t.Length == 0 || t == "\"\"" || t == "{}") return "";
        if (t.StartsWith('{') || t.StartsWith('['))
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                return ExtractText(doc.RootElement);
            }
            catch { /* not valid JSON → use raw */ }
        return t.Trim('"');
    }

    private static string ExtractText(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => e.GetString() ?? "",
        System.Text.Json.JsonValueKind.Object => e.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
        System.Text.Json.JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(ExtractText)),
        _ => "",
    };
}
