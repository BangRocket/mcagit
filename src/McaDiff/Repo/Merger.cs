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
    /// <summary>True when the merge stopped on conflicts without committing (awaiting resolution).</summary>
    public bool Stopped { get; init; }
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
    /// <param name="autoResolve">When true, conflicts are resolved (keeping ours, or
    /// theirs when <paramref name="preferTheirs"/>) and the merge commits immediately —
    /// git's <c>-X ours/theirs</c>. When false, a conflicted merge stops and records
    /// MERGE_HEAD for <c>merge --continue</c>/<c>--abort</c>.</param>
    public static MergeResult Merge(Repository repo, string theirRef, bool preferTheirs, bool autoResolve, string author)
    {
        string branch = repo.CurrentBranch()
            ?? throw new InvalidOperationException("merge requires being on a branch (detached HEAD).");
        if (repo.InMerge)
            throw new InvalidOperationException("a merge is already in progress (use merge --continue or --abort)");
        string theirs = repo.ResolveRef(theirRef);
        string? ours = repo.ReadBranch(branch);

        if (ours is null) { FastForwardTo(repo, branch, null, theirs, theirRef); return new MergeResult { CommitHash = theirs, FastForward = true }; }
        if (ours == theirs) return new MergeResult { CommitHash = ours, AlreadyUpToDate = true };

        List<string> bases = MergeBase.FindAll(repo, ours, theirs);
        if (bases.Contains(ours)) { FastForwardTo(repo, branch, ours, theirs, theirRef); return new MergeResult { CommitHash = theirs, FastForward = true }; }
        if (bases.Contains(theirs)) return new MergeResult { CommitHash = ours, AlreadyUpToDate = true };

        Manifest baseM = BaseManifest(repo, bases, out string? baseCommit);
        Manifest oursM = repo.ReadManifest(repo.ReadCommit(ours).Tree);
        Manifest theirsM = repo.ReadManifest(repo.ReadCommit(theirs).Tree);

        var conflicts = new List<MergeConflict>();
        Manifest merged = MergeManifests(repo, baseM, oursM, theirsM, preferTheirs, conflicts);
        string treeHash = repo.WriteManifest(merged);
        string msg = $"Merge {theirRef} into {branch}";

        if (conflicts.Count == 0 || autoResolve)
        {
            string commit = repo.CreateCommit(treeHash, [ours, theirs], msg, author);
            return new MergeResult { CommitHash = commit, Conflicts = conflicts };
        }

        // Stop: record the in-progress merge and lay the partial result into the worktree.
        repo.BeginMergeState(ours, theirs, baseCommit, msg, conflicts);
        if (repo.Worktree is { } w) Checkout.Materialize(repo, merged, w, prune: true);
        return new MergeResult { Conflicts = conflicts, Stopped = true };
    }

    private static void FastForwardTo(Repository repo, string branch, string? from, string to, string theirRef)
    {
        repo.WriteBranch(branch, to);
        repo.RecordHead(from, to, $"merge {theirRef}: Fast-forward");
        if (repo.Worktree is { } w) Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(to).Tree), w, prune: true);
    }

    /// <summary>Completes a stopped merge by snapshotting the (resolved) worktree.</summary>
    public static MergeResult Continue(Repository repo, string author)
    {
        if (!repo.InMerge) throw new InvalidOperationException("no merge in progress");
        string theirs = repo.ReadMergeHead()!;
        string ours = repo.HeadCommit() ?? throw new InvalidOperationException("no HEAD");
        string world = repo.Worktree
            ?? throw new InvalidOperationException("merge --continue needs a bound worktree to snapshot the resolution");

        Manifest m = Snapshotter.Snapshot(repo, world);
        string tree = repo.WriteManifest(m);
        string msg = repo.ReadMergeMsg() ?? $"Merge {theirs[..10]}";
        string commit = repo.CreateCommit(tree, [ours, theirs], msg, author);
        repo.ClearMergeState();
        return new MergeResult { CommitHash = commit };
    }

    /// <summary>Aborts a stopped merge, restoring the pre-merge branch tip and worktree.</summary>
    public static void Abort(Repository repo)
    {
        if (!repo.InMerge) throw new InvalidOperationException("no merge in progress");
        string orig = repo.ReadOrigHead() ?? throw new InvalidOperationException("ORIG_HEAD missing — cannot abort");
        string branch = repo.CurrentBranch() ?? throw new InvalidOperationException("abort requires being on a branch");
        string? from = repo.HeadCommit();

        repo.WriteBranch(branch, orig);
        if (repo.Worktree is { } w) Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(orig).Tree), w, prune: true);
        repo.RecordHead(from, orig, "merge --abort");
        repo.ClearMergeState();
    }

    /// <summary>The 3-way base manifest, using a recursively-merged virtual base when the
    /// histories criss-cross (more than one merge base).</summary>
    private static Manifest BaseManifest(Repository repo, List<string> bases, out string? baseCommit)
    {
        if (bases.Count == 0) { baseCommit = null; return new Manifest(); }
        baseCommit = bases.Count == 1 ? bases[0] : VirtualBase(repo, bases);
        return repo.ReadManifest(repo.ReadCommit(baseCommit).Tree);
    }

    /// <summary>Folds multiple merge bases into one virtual base commit (git's recursive
    /// strategy): merge the bases pairwise over their own recursive base, writing an
    /// unreferenced commit so further merge-base math sees a real DAG node.</summary>
    private static string VirtualBase(Repository repo, List<string> bases)
    {
        string acc = bases[0];
        for (int i = 1; i < bases.Count; i++)
        {
            List<string> sub = MergeBase.FindAll(repo, acc, bases[i]);
            Manifest bm = BaseManifest(repo, sub, out _);
            Manifest om = repo.ReadManifest(repo.ReadCommit(acc).Tree);
            Manifest tm = repo.ReadManifest(repo.ReadCommit(bases[i]).Tree);
            Manifest merged = MergeManifests(repo, bm, om, tm, preferTheirs: false, []);
            string tree = repo.WriteManifest(merged);
            acc = repo.WriteCommit(new CommitObject
            {
                Tree = tree,
                Parents = [acc, bases[i]],
                Message = "virtual merge base",
                Author = "mcadiff",
                Time = "1970-01-01T00:00:00.0000000+00:00", // fixed ⇒ deterministic, dedupable
            });
        }
        return acc;
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
            bool inB = bm is not null, inO = om is not null, inT = tm is not null;

            if (inO && inT) // present on both → merge chunk-by-chunk (may be empty)
            {
                var map = MergeChunkMap(repo, rel, bm, om, tm, preferTheirs, conflicts);
                merged.Regions[rel] = new SortedDictionary<string, string>(map, StringComparer.Ordinal);
            }
            else if (inO) // only ours has the region
            {
                if (!inB) merged.Regions[rel] = om!;                  // ours added it
                else if (MapsEqual(om, bm)) { /* theirs deleted, ours unchanged → delete */ }
                else { conflicts.Add(new MergeConflict(rel, null, "", "delete/modify conflict — kept ours")); merged.Regions[rel] = om!; }
            }
            else if (inT) // only theirs has the region
            {
                if (!inB) merged.Regions[rel] = tm!;                  // theirs added it
                else if (MapsEqual(tm, bm)) { /* ours deleted, theirs unchanged → delete */ }
                else conflicts.Add(new MergeConflict(rel, null, "", "delete/modify conflict — kept ours (deleted)"));
            }
            // present in base only (both deleted) → absent in merged
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
        // Sorted so a parent op ("X") applies before a child op ("X.Y") and the result is
        // deterministic (HashSet order isn't) — which also surfaces the parent/child conflict below.
        foreach (string path in Union(ours.Keys, theirs.Keys, null).OrderBy(p => p, StringComparer.Ordinal))
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
            ApplyOp(ref mergedRoot, chosen, file, chunk, conflicts);
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

    private static void ApplyOp(ref NbtCompound root, PatchOp op, string file, string? chunk, List<MergeConflict> conflicts)
    {
        if (op.IsRoot)
        {
            root = op.Value is null ? new NbtCompound("") : (NbtCompound)NbtJson.FromJson(op.Value, "");
            return;
        }
        string? name = NbtPath.TerminalName(op.Path);
        NbtTag? tag = op.Value is null ? null : NbtJson.FromJson(op.Value, name);
        // Don't discard Set's result: a false means the parent container is missing/wrong-type, so
        // the change couldn't be applied — that's a conflict (silently dropping it loses merge data).
        if (!NbtPath.Set(root, op.Path, tag))
            conflicts.Add(new MergeConflict(file, chunk, op.Path, "parent path missing during merge apply"));
    }

    /// <summary>Whether two NbtJson-encoded values are semantically equal — compared as NBT
    /// (so two compounds with the same content but different key order are equal), not by
    /// JSON string (which is key-order-sensitive and would raise spurious conflicts).</summary>
    private static bool SameValue(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        try { return NbtEquality.DeepEquals(NbtJson.FromJson(a, null), NbtJson.FromJson(b, null)); }
        catch { return a.ToJsonString() == b.ToJsonString(); } // malformed → fall back to literal compare
    }

    private static bool MapsEqual(SortedDictionary<string, string>? a, SortedDictionary<string, string>? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out string? v) || v != kv.Value) return false;
        return true;
    }

    private static IEnumerable<string> Union(IEnumerable<string>? a, IEnumerable<string>? b, IEnumerable<string>? c)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (a is not null) set.UnionWith(a);
        if (b is not null) set.UnionWith(b);
        if (c is not null) set.UnionWith(c);
        return set;
    }
}
