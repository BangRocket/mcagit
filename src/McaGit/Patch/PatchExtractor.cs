using fNbt;
using McaGit.Anvil;
using McaGit.Diff;
using McaGit.Model;
using McaGit.Nbt;

namespace McaGit.Patch;

/// <summary>
/// Builds a <see cref="Patch"/> from a base→target world pair. Reuses the diff's
/// file/chunk matching and the <see cref="NbtComparer"/> walk (via
/// <see cref="PatchOpSink"/>), but captures typed, applyable ops. <c>--whole-chunk</c>
/// / <c>--whole-file</c> emit a single root op per unit instead of node ops.
/// </summary>
public static class PatchExtractor
{
    public static WorldPatch Extract(string basePath, string targetPath, DiffRunOptions options,
                                bool wholeChunk = false, bool wholeFile = false)
    {
        var patch = new WorldPatch
        {
            Base = basePath,
            Target = targetPath,
            BaseDataVersion = WorldSource.DataVersion(basePath),
            TargetDataVersion = WorldSource.DataVersion(targetPath),
        };

        bool dirA = WorldSource.IsDirectory(basePath);
        bool dirB = WorldSource.IsDirectory(targetPath);

        if (!dirA && !dirB)
        {
            PatchFileEntry? e = ExtractModified(WorldSource.ResolveFile(basePath),
                                                WorldSource.ResolveFile(targetPath), wholeChunk, wholeFile);
            if (e is not null) patch.Files.Add(e);
            return patch;
        }
        if (dirA != dirB)
            throw new ArgumentException("Both inputs must be the same kind: two world folders or two files.");

        Dictionary<string, WorldUnit> unitsA = WorldSource.Enumerate(basePath, options);
        Dictionary<string, WorldUnit> unitsB = WorldSource.Enumerate(targetPath, options);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        keys.UnionWith(unitsA.Keys);
        keys.UnionWith(unitsB.Keys);

        foreach (string k in keys)
        {
            unitsA.TryGetValue(k, out WorldUnit? ua);
            unitsB.TryGetValue(k, out WorldUnit? ub);
            PatchFileEntry? e = (ua, ub) switch
            {
                (not null, null) => ExtractWholeFile(ua, DiffStatus.Removed),
                (null, not null) => ExtractWholeFile(ub, DiffStatus.Added),
                _ => ExtractModified(ua!, ub!, wholeChunk, wholeFile),
            };
            if (e is not null) patch.Files.Add(e);
        }
        return patch;
    }

    private static PatchFileEntry? ExtractModified(WorldUnit a, WorldUnit b, bool wholeChunk, bool wholeFile)
        => a.Category == "blob"      // non-NBT (JSON, .mcc): not representable as node ops
            ? null
            : a.Kind == UnitKind.Region
                ? ExtractRegion(a, b, wholeChunk)
                : ExtractLoose(a, b, wholeFile);

    private static PatchFileEntry? ExtractRegion(WorldUnit a, WorldUnit b, bool wholeChunk)
    {
        byte[] bytesA = File.ReadAllBytes(a.AbsolutePath);
        byte[] bytesB = File.ReadAllBytes(b.AbsolutePath);
        if (bytesA.AsSpan().SequenceEqual(bytesB)) return null;

        RegionFile regA = RegionFile.Parse(a.AbsolutePath, bytesA);
        RegionFile regB = RegionFile.Parse(b.AbsolutePath, bytesB);

        var positions = new SortedSet<ChunkPos>();
        foreach (RawChunk c in regA.Chunks) positions.Add(c.Pos);
        foreach (RawChunk c in regB.Chunks) positions.Add(c.Pos);

        var chunks = new List<ChunkPatch>();
        foreach (ChunkPos pos in positions)
        {
            try
            {
                bool inA = regA.TryGet(pos, out RawChunk ca);
                bool inB = regB.TryGet(pos, out RawChunk cb);

                if (inA && !inB)
                    chunks.Add(RootChunk(pos, DiffStatus.Removed, ChunkCodec.Decode(ca), null, ca.Timestamp));
                else if (!inA && inB)
                    chunks.Add(RootChunk(pos, DiffStatus.Added, null, ChunkCodec.Decode(cb), cb.Timestamp));
                else
                {
                    if (ca.PayloadEquals(cb)) continue;
                    NbtCompound rootA = ChunkCodec.Decode(ca);
                    NbtCompound rootB = ChunkCodec.Decode(cb);
                    if (wholeChunk)
                    {
                        chunks.Add(RootChunk(pos, DiffStatus.Modified, rootA, rootB, cb.Timestamp));
                    }
                    else
                    {
                        var sink = new PatchOpSink();
                        NbtComparer.Walk(rootA, rootB, sink);
                        if (sink.Ops.Count > 0)
                            chunks.Add(new ChunkPatch { X = pos.X, Z = pos.Z, Status = DiffStatus.Modified, Timestamp = cb.Timestamp, Ops = sink.Ops });
                    }
                }
            }
            catch (UnsupportedChunkException) { /* can't patch an undecodable chunk — skip */ }
        }

        return chunks.Count == 0
            ? null
            : new PatchFileEntry { Path = b.RelativePath, Kind = UnitKind.Region, Status = DiffStatus.Modified, Chunks = chunks };
    }

    private static PatchFileEntry? ExtractLoose(WorldUnit a, WorldUnit b, bool wholeFile)
    {
        byte[] bytesA = File.ReadAllBytes(a.AbsolutePath);
        byte[] bytesB = File.ReadAllBytes(b.AbsolutePath);
        if (bytesA.AsSpan().SequenceEqual(bytesB)) return null;

        NbtCompound rootA = ChunkCodec.LoadNbtFile(a.AbsolutePath);
        NbtCompound rootB = ChunkCodec.LoadNbtFile(b.AbsolutePath);

        List<PatchOp> ops;
        if (wholeFile)
        {
            ops = [new PatchOp { Path = "", Base = NbtJson.ToJson(rootA), Value = NbtJson.ToJson(rootB) }];
        }
        else
        {
            var sink = new PatchOpSink();
            NbtComparer.Walk(rootA, rootB, sink);
            if (sink.Ops.Count == 0) return null;
            ops = sink.Ops;
        }

        return new PatchFileEntry { Path = b.RelativePath, Kind = UnitKind.Loose, Status = DiffStatus.Modified, Ops = ops };
    }

    private static PatchFileEntry? ExtractWholeFile(WorldUnit unit, DiffStatus status)
    {
        if (unit.Category == "blob") return null; // non-NBT blobs aren't carried in a patch
        // status==Added means present only in target; ==Removed means only in base.
        bool added = status == DiffStatus.Added;
        if (unit.Kind == UnitKind.Loose)
        {
            NbtCompound root = ChunkCodec.LoadNbtFile(unit.AbsolutePath);
            JsonOpValues(added, NbtJson.ToJson(root), out var bas, out var val);
            return new PatchFileEntry
            {
                Path = unit.RelativePath,
                Kind = UnitKind.Loose,
                Status = status,
                Ops = [new PatchOp { Path = "", Base = bas, Value = val }],
            };
        }

        RegionFile region = RegionFile.Open(unit.AbsolutePath);
        var chunks = new List<ChunkPatch>();
        foreach (RawChunk c in region.Chunks)
        {
            try
            {
                NbtCompound root = ChunkCodec.Decode(c);
                chunks.Add(added
                    ? RootChunk(c.Pos, DiffStatus.Added, null, root, c.Timestamp)
                    : RootChunk(c.Pos, DiffStatus.Removed, root, null, c.Timestamp));
            }
            catch (UnsupportedChunkException) { /* skip */ }
        }
        // Emit even with zero chunks: an empty/0-byte region (e.g. fresh poi files)
        // is still a file-level add/remove the applier must reproduce.
        return new PatchFileEntry { Path = unit.RelativePath, Kind = UnitKind.Region, Status = status, Chunks = chunks };
    }

    private static ChunkPatch RootChunk(ChunkPos pos, DiffStatus status, NbtCompound? oldRoot, NbtCompound? newRoot, int timestamp)
        => new()
        {
            X = pos.X,
            Z = pos.Z,
            Status = status,
            Timestamp = timestamp,
            Ops = [new PatchOp
            {
                Path = "",
                Base = oldRoot is null ? null : NbtJson.ToJson(oldRoot),
                Value = newRoot is null ? null : NbtJson.ToJson(newRoot),
            }],
        };

    private static void JsonOpValues(bool added, System.Text.Json.Nodes.JsonNode root,
        out System.Text.Json.Nodes.JsonNode? bas, out System.Text.Json.Nodes.JsonNode? val)
    {
        bas = added ? null : root;
        val = added ? root : null;
    }
}
