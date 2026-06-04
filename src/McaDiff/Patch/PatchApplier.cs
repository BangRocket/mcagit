using System.Text.Json.Nodes;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Model;
using McaDiff.Nbt;

namespace McaDiff.Patch;

/// <summary>Options for an apply run.</summary>
public sealed record ApplySettings(
    bool Reverse = false,
    bool Force = false,
    bool DryRun = false,
    IReadOnlySet<string>? OnlyCategories = null);

/// <summary>A node that could not be applied because the target didn't match the patch.</summary>
public sealed record Conflict(string File, string? Chunk, string Path, string Reason);

/// <summary>Outcome of an apply run.</summary>
public sealed class ApplyReport
{
    public int Applied { get; private set; }
    public int FilesWritten { get; private set; }
    public List<Conflict> Conflicts { get; } = [];

    public bool HasConflicts => Conflicts.Count > 0;
    internal void Apply() => Applied++;
    internal void Wrote() => FilesWritten++;
    internal void Conflict(string file, string? chunk, string path, string reason)
        => Conflicts.Add(new Conflict(file, chunk, path, reason));
}

/// <summary>
/// Applies a <see cref="Patch"/> to a target world non-destructively: the target
/// is copied to a fresh output directory, then only the patched nodes are
/// rewritten — each guarded by a 3-way check (current must equal the expected
/// value) unless <c>--force</c>. <c>--reverse</c> swaps base/value (restore the
/// old state); <c>--dry-run</c> reports without writing.
/// </summary>
public static class PatchApplier
{
    public static ApplyReport Apply(WorldPatch patch, string targetDir, string outputDir, ApplySettings settings)
    {
        if (patch.Version != 1)
            throw new NotSupportedException($"unsupported .mcapatch version {patch.Version} (this build reads version 1)");
        if (!WorldSource.IsDirectory(targetDir))
            throw new ArgumentException("apply requires a world directory as the target.");

        if (!settings.DryRun)
        {
            if (Directory.Exists(outputDir) && Directory.EnumerateFileSystemEntries(outputDir).Any())
                throw new ArgumentException($"output directory is not empty: {outputDir}");
            CopyDirectory(targetDir, outputDir);
        }

        string workRoot = settings.DryRun ? targetDir : outputDir;
        var report = new ApplyReport();

        foreach (PatchFileEntry entry in patch.Files)
        {
            if (settings.OnlyCategories is { } only && !only.Contains(Category(entry)))
                continue;

            if (entry.Kind == UnitKind.Region)
                ApplyRegion(entry, workRoot, settings, report);
            else
                ApplyLoose(entry, workRoot, settings, report);
        }

        return report;
    }

    private static void ApplyRegion(PatchFileEntry entry, string workRoot, ApplySettings s, ApplyReport report)
    {
        string outFile = PathGuard.Confine(workRoot, entry.Path); // reject ../ escapes from an untrusted patch
        var chunks = new Dictionary<ChunkPos, RawChunk>();
        if (File.Exists(outFile))
            foreach (RawChunk rc in RegionFile.Open(outFile).Chunks)
                chunks[rc.Pos] = rc;

        bool changed = false;
        foreach (ChunkPatch cp in entry.Chunks ?? [])
        {
            var pos = new ChunkPos(cp.X, cp.Z);

            if (cp.Ops is [{ IsRoot: true } rootOp])
            {
                NbtCompound? current = chunks.TryGetValue(pos, out RawChunk? rc0) ? TryDecode(rc0) : null;
                (JsonNode? expected, JsonNode? desired) = Direction(rootOp, s.Reverse);
                NbtTag? expectedTag = expected is null ? null : NbtJson.FromJson(expected, null);

                if (!s.Force && !NbtEquality.DeepEquals(current, expectedTag))
                {
                    report.Conflict(entry.Path, pos.ToString(), "", "chunk does not match expected base");
                    continue;
                }

                if (desired is null)
                {
                    chunks.Remove(pos);
                }
                else if (!s.DryRun)
                {
                    var newRoot = (NbtCompound)NbtJson.FromJson(desired, ""); // root tag must be named ("")
                    chunks[pos] = new RawChunk(pos, ChunkCompression.ZLib, ChunkCodec.Encode(newRoot, ChunkCompression.ZLib), false, cp.Timestamp);
                }
                report.Apply();
                changed = true;
            }
            else
            {
                if (!chunks.TryGetValue(pos, out RawChunk? rc1) || TryDecode(rc1) is not { } root)
                {
                    foreach (PatchOp op in cp.Ops) report.Conflict(entry.Path, pos.ToString(), op.Path, "chunk missing or undecodable");
                    continue;
                }

                bool any = false;
                foreach (PatchOp op in cp.Ops)
                {
                    (bool ok, string? reason) = ApplyNodeOp(root, op, s.Reverse, s.Force);
                    if (ok) { report.Apply(); any = true; }
                    else report.Conflict(entry.Path, pos.ToString(), op.Path, reason!);
                }
                if (any && !s.DryRun)
                {
                    chunks[pos] = new RawChunk(pos, ChunkCompression.ZLib, ChunkCodec.Encode(root, ChunkCompression.ZLib), false, rc1.Timestamp);
                    changed = true;
                }
            }
        }

        if (s.DryRun) return;

        if (changed)
        {
            if (chunks.Count == 0) { if (File.Exists(outFile)) File.Delete(outFile); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(outFile)!); RegionWriter.Write(outFile, chunks.Values); }
            report.Wrote();
            return;
        }

        // No chunk-level change happened — but a whole-file add/remove of an empty
        // region (no decodable chunks) still needs the file to exist/not exist.
        DiffStatus eff = s.Reverse ? Flip(entry.Status) : entry.Status;
        if (eff == DiffStatus.Added && !File.Exists(outFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            RegionWriter.Write(outFile, chunks.Values); // empty region (8 KiB header)
            report.Apply();
            report.Wrote();
        }
        else if (eff == DiffStatus.Removed && chunks.Count == 0 && File.Exists(outFile))
        {
            File.Delete(outFile);
            report.Apply();
            report.Wrote();
        }
    }

    private static DiffStatus Flip(DiffStatus s) => s switch
    {
        DiffStatus.Added => DiffStatus.Removed,
        DiffStatus.Removed => DiffStatus.Added,
        _ => DiffStatus.Modified,
    };

    private static void ApplyLoose(PatchFileEntry entry, string workRoot, ApplySettings s, ApplyReport report)
    {
        string outFile = PathGuard.Confine(workRoot, entry.Path); // reject ../ escapes from an untrusted patch
        NbtCompound? root = File.Exists(outFile) ? ChunkCodec.LoadNbtFile(outFile) : null;
        List<PatchOp> ops = entry.Ops ?? [];

        if (ops is [{ IsRoot: true } rootOp])
        {
            (JsonNode? expected, JsonNode? desired) = Direction(rootOp, s.Reverse);
            NbtTag? expectedTag = expected is null ? null : NbtJson.FromJson(expected, null);
            if (!s.Force && !NbtEquality.DeepEquals(root, expectedTag))
            {
                report.Conflict(entry.Path, null, "", "file does not match expected base");
                return;
            }
            if (!s.DryRun)
            {
                if (desired is null) { if (File.Exists(outFile)) File.Delete(outFile); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(outFile)!); ChunkCodec.SaveNbtFile(outFile, (NbtCompound)NbtJson.FromJson(desired, "")); }
            }
            report.Apply();
            report.Wrote();
            return;
        }

        if (root is null)
        {
            foreach (PatchOp op in ops) report.Conflict(entry.Path, null, op.Path, "file missing");
            return;
        }

        bool any = false;
        foreach (PatchOp op in ops)
        {
            (bool ok, string? reason) = ApplyNodeOp(root, op, s.Reverse, s.Force);
            if (ok) { report.Apply(); any = true; }
            else report.Conflict(entry.Path, null, op.Path, reason!);
        }
        if (any && !s.DryRun) { ChunkCodec.SaveNbtFile(outFile, root); report.Wrote(); }
    }

    /// <summary>Applies one node op to a live tree with the 3-way guard.</summary>
    private static (bool Ok, string? Reason) ApplyNodeOp(NbtTag root, PatchOp op, bool reverse, bool force)
    {
        (JsonNode? expected, JsonNode? desired) = Direction(op, reverse);
        string? name = NbtPath.TerminalName(op.Path);
        NbtTag? expectedTag = expected is null ? null : NbtJson.FromJson(expected, name);
        NbtTag? current = NbtPath.Get(root, op.Path);

        if (!force && !NbtEquality.DeepEquals(current, expectedTag))
            return (false, "value does not match expected base");

        NbtTag? desiredTag = desired is null ? null : NbtJson.FromJson(desired, name);
        return NbtPath.Set(root, op.Path, desiredTag) ? (true, null) : (false, "parent path missing");
    }

    private static (JsonNode? Expected, JsonNode? Desired) Direction(PatchOp op, bool reverse)
        => reverse ? (op.Value, op.Base) : (op.Base, op.Value);

    private static NbtCompound? TryDecode(RawChunk rc)
    {
        try { return ChunkCodec.Decode(rc); }
        catch (UnsupportedChunkException) { return null; }
    }

    private static string Category(PatchFileEntry entry)
    {
        if (entry.Kind == UnitKind.Loose) return "nbt";
        string p = "/" + entry.Path;
        if (p.Contains("/entities/")) return "entities";
        if (p.Contains("/poi/")) return "poi";
        return "region";
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
    }
}
