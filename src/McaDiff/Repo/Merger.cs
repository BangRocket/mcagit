using fNbt;
using McaDiff.Diff;
using McaDiff.Nbt;
using McaDiff.Patch;

namespace McaDiff.Repo;

public sealed record MergeConflict(string File, string? Chunk, string Path, string Reason);

public sealed class MergeResult
{
    public string? CommitHash { get; init; }
    public bool AlreadyUpToDate { get; init; }
    public bool FastForward { get; init; }
    public List<MergeConflict> Conflicts { get; init; } = [];
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// True 3-way merge of another branch into the current one. Resolves trivially
/// where only one side changed; for nodes both sides changed it merges per NBT
/// node (reusing the diff walk + patch ops) — disjoint paths combine, genuine
/// clashes keep "ours" (or "theirs" with <c>preferTheirs</c>) and are reported.
/// </summary>
public static class Merger
{
    public static MergeResult Merge(Repository repo, string theirRef, bool preferTheirs, string author)
    {
        string branch = repo.CurrentBranch()
            ?? throw new InvalidOperationException("merge requires being on a branch (detached HEAD).");
        string theirs = repo.ResolveRef(theirRef);
        string? ours = repo.ReadBranch(branch);

        if (ours is null) { repo.WriteBranch(branch, theirs); return new MergeResult { CommitHash = theirs, FastForward = true }; }
        if (ours == theirs) return new MergeResult { CommitHash = ours, AlreadyUpToDate = true };

        string? mergeBase = MergeBase.Find(repo, ours, theirs);
        if (mergeBase == ours) { repo.WriteBranch(branch, theirs); return new MergeResult { CommitHash = theirs, FastForward = true }; }
        if (mergeBase == theirs) return new MergeResult { CommitHash = ours, AlreadyUpToDate = true };

        Manifest baseM = mergeBase is not null ? repo.ReadManifest(repo.ReadCommit(mergeBase).Tree) : new Manifest();
        Manifest oursM = repo.ReadManifest(repo.ReadCommit(ours).Tree);
        Manifest theirsM = repo.ReadManifest(repo.ReadCommit(theirs).Tree);

        var conflicts = new List<MergeConflict>();
        Manifest merged = MergeManifests(repo, baseM, oursM, theirsM, preferTheirs, conflicts);

        string treeHash = repo.WriteManifest(merged);
        string commit = repo.CreateCommit(treeHash, [ours, theirs], $"Merge {theirRef} into {branch}", author);
        return new MergeResult { CommitHash = commit, Conflicts = conflicts };
    }

    /// <summary>
    /// Three-way merges three manifests (base, ours, theirs) into one, recording
    /// conflicts. Exposed so revert/cherry-pick can reuse the per-node merge.
    /// </summary>
    public static Manifest MergeManifests(Repository repo, Manifest b, Manifest o, Manifest t, bool preferTheirs, List<MergeConflict> conflicts)
    {
        var merged = new Manifest();

        foreach (string rel in Union(o.Regions.Keys, t.Regions.Keys, b.Regions.Keys))
        {
            var bm = b.Regions.GetValueOrDefault(rel);
            var om = o.Regions.GetValueOrDefault(rel);
            var tm = t.Regions.GetValueOrDefault(rel);
            var map = MergeChunkMap(repo, rel, bm, om, tm, preferTheirs, conflicts);
            if (map.Count > 0 || (om is not null && tm is not null)) // keep empty regions that exist on both sides
                merged.Regions[rel] = new SortedDictionary<string, string>(map, StringComparer.Ordinal);
        }

        MergeScalarSet(repo, b.Nbt, o.Nbt, t.Nbt, preferTheirs, conflicts, nodeMerge: true, merged.Nbt);
        MergeScalarSet(repo, b.Blobs, o.Blobs, t.Blobs, preferTheirs, conflicts, nodeMerge: false, merged.Blobs);
        return merged;
    }

    private static Dictionary<string, string> MergeChunkMap(
        Repository repo, string rel,
        SortedDictionary<string, string>? b, SortedDictionary<string, string>? o, SortedDictionary<string, string>? t,
        bool preferTheirs, List<MergeConflict> conflicts)
    {
        var result = new Dictionary<string, string>();
        foreach (string key in Union(o?.Keys, t?.Keys, b?.Keys))
        {
            string? bh = b?.GetValueOrDefault(key);
            string? oh = o?.GetValueOrDefault(key);
            string? th = t?.GetValueOrDefault(key);
            string? merged = MergeHash(repo, bh, oh, th, preferTheirs, nodeMerge: true, rel, key, conflicts);
            if (merged is not null) result[key] = merged;
        }
        return result;
    }

    private static void MergeScalarSet(
        Repository repo, SortedDictionary<string, string> b, SortedDictionary<string, string> o, SortedDictionary<string, string> t,
        bool preferTheirs, List<MergeConflict> conflicts, bool nodeMerge, SortedDictionary<string, string> into)
    {
        foreach (string rel in Union(o.Keys, t.Keys, b.Keys))
        {
            string? merged = MergeHash(repo,
                b.GetValueOrDefault(rel), o.GetValueOrDefault(rel), t.GetValueOrDefault(rel),
                preferTheirs, nodeMerge, rel, null, conflicts);
            if (merged is not null) into[rel] = merged;
        }
    }

    /// <summary>Three-way resolve of a single content hash (chunk or file).</summary>
    private static string? MergeHash(Repository repo, string? b, string? o, string? t,
        bool preferTheirs, bool nodeMerge, string file, string? chunk, List<MergeConflict> conflicts)
    {
        if (o == t) return o;          // both sides identical (incl. both absent)
        if (o == b) return t;          // only theirs changed (incl. their delete)
        if (t == b) return o;          // only ours changed

        // Both sides changed differently.
        if (o is null || t is null)    // delete/modify — keep ours, report
        {
            conflicts.Add(new MergeConflict(file, chunk, "", "delete/modify conflict — kept ours"));
            return o;
        }
        if (!nodeMerge)                // binary blob — pick a side, report
        {
            conflicts.Add(new MergeConflict(file, chunk, "", "binary conflict — kept " + (preferTheirs ? "theirs" : "ours")));
            return preferTheirs ? t : o;
        }
        return MergeNode(repo, b, o, t, preferTheirs, file, chunk, conflicts);
    }

    /// <summary>Per-NBT-node 3-way merge of two changed roots over a common base.</summary>
    private static string MergeNode(Repository repo, string? baseHash, string ourHash, string theirHash,
        bool preferTheirs, string file, string? chunk, List<MergeConflict> conflicts)
    {
        NbtCompound baseRoot = baseHash is not null ? NbtCanonical.Deserialize(repo.Objects.Read(baseHash)) : new NbtCompound("");
        NbtCompound ourRoot = NbtCanonical.Deserialize(repo.Objects.Read(ourHash));
        NbtCompound theirRoot = NbtCanonical.Deserialize(repo.Objects.Read(theirHash));

        Dictionary<string, PatchOp> ours = OpsByPath(baseRoot, ourRoot);
        Dictionary<string, PatchOp> theirs = OpsByPath(baseRoot, theirRoot);

        var mergedRoot = (NbtCompound)baseRoot.Clone();
        foreach (string path in Union(ours.Keys, theirs.Keys, null))
        {
            ours.TryGetValue(path, out PatchOp? oo);
            theirs.TryGetValue(path, out PatchOp? to);
            PatchOp chosen;
            if (oo is not null && to is not null)
            {
                if (SameValue(oo.Value, to.Value)) chosen = oo;
                else { conflicts.Add(new MergeConflict(file, chunk, path, "kept " + (preferTheirs ? "theirs" : "ours"))); chosen = preferTheirs ? to : oo; }
            }
            else chosen = oo ?? to!;
            ApplyOp(ref mergedRoot, chosen);
        }

        return repo.Objects.Write(NbtCanonical.Serialize(mergedRoot));
    }

    private static Dictionary<string, PatchOp> OpsByPath(NbtCompound baseRoot, NbtCompound target)
    {
        var sink = new PatchOpSink();
        NbtComparer.Walk(baseRoot, target, sink);
        var byPath = new Dictionary<string, PatchOp>(sink.Ops.Count);
        foreach (PatchOp op in sink.Ops) byPath[op.Path] = op;
        return byPath;
    }

    private static void ApplyOp(ref NbtCompound root, PatchOp op)
    {
        if (op.IsRoot)
        {
            root = op.Value is null ? new NbtCompound("") : (NbtCompound)NbtJson.FromJson(op.Value, "");
            return;
        }
        string? name = NbtPath.TerminalName(op.Path);
        NbtTag? tag = op.Value is null ? null : NbtJson.FromJson(op.Value, name);
        NbtPath.Set(root, op.Path, tag);
    }

    private static bool SameValue(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
        => (a is null && b is null) || (a is not null && b is not null && a.ToJsonString() == b.ToJsonString());

    private static IEnumerable<string> Union(IEnumerable<string>? a, IEnumerable<string>? b, IEnumerable<string>? c)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (a is not null) set.UnionWith(a);
        if (b is not null) set.UnionWith(b);
        if (c is not null) set.UnionWith(c);
        return set;
    }
}
