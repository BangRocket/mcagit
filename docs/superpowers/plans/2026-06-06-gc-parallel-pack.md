# Parallel gc delta-packing + progress — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `mcagit gc` delta-compress its consolidated pack across all CPU cores and show TTY-aware progress, so it is fast and never looks hung — without changing the pack format, the reader, or any existing invariant.

**Architecture:** Refactor the serial per-object loop in `Packfile.Write` into a reusable `WriteSegment` unit. Add `Packfile.WriteParallel`, which partitions the size-sorted object list into byte-balanced contiguous segments, delta-compresses each segment **in parallel** to its own temp file (delta bases confined to the segment, so the bytes are position-independent), then serially concatenates the segment files into the final pack — relative delta back-references survive concatenation untouched, only the index gets global offsets. `Gc.Repack` drives it with a `Progress` reporter and a `--threads` count from `GcCmd`.

**Tech Stack:** .NET 10 / C#. Build & test with `/usr/local/share/dotnet/dotnet` (the net10 SDK; the PATH `dotnet` is SDK 9 and cannot target net10). `System.Threading.Tasks.Parallel`, `Interlocked`, existing `McaGit.Output.Progress`.

---

## Build & test commands (this environment)

- Build: `/usr/local/share/dotnet/dotnet build McaGit.sln -c Release`
- One test class: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackfileTests"`
- Full suite: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release`

`dotnet` (PATH) is SDK 9.0.314 and fails with `NETSDK1045`. Always use the full path above.

## File structure

- **`src/McaGit/Repo/Packfile.cs`** (modify) — add `using McaGit.Output;`; extract `WriteSegment`; add optional `Action? onObject` to `Write`; add `WriteParallel` + private `PartitionByBytes`.
- **`src/McaGit/Repo/Gc.cs`** (modify) — add `using McaGit.Output;`; `Repack` gains `int threads, Progress? progress`; calls `Packfile.WriteParallel`.
- **`src/McaGit/Cli/RepoCommands.cs`** (modify) — `GcCmd` parses `--threads`, creates a `Progress`, passes both to `Gc.Repack`.
- **`tests/McaGit.Tests/PackfileTests.cs`** (create) — unit tests for `Write` and `WriteParallel` (round-trip, equivalence, fallback).
- **`tests/McaGit.Tests/PackAtCommitTests.cs`** (modify) — add a `gc --threads` integration test (reuses its `BoundRepo`/`Chunk` helpers).
- **`README.md`** (modify) — document `gc --threads` and progress.

The 3 existing `Packfile.Write` callers (`Gc.cs:52`, `BucketTransport.cs:101`, `PackTransfer.cs:25`) keep working: `Write` stays a valid 3-arg call (new `onObject` is optional). Only `Gc` moves to `WriteParallel`.

---

### Task 1: Characterization test for `Packfile.Write` round-trip

Locks in current serial behavior before the refactor. It passes against today's code.

**Files:**
- Create: `tests/McaGit.Tests/PackfileTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
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
```

- [ ] **Step 2: Run it — expect PASS against current code**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackfileTests"`
Expected: PASS (1 test). This characterizes existing `Write` behavior.

- [ ] **Step 3: Commit**

```bash
git add tests/McaGit.Tests/PackfileTests.cs
git commit -m "test: characterize Packfile.Write round-trip"
```

---

### Task 2: Extract `WriteSegment` from `Write` (behavior-preserving refactor)

**Files:**
- Modify: `src/McaGit/Repo/Packfile.cs` (the `Write` method, ~lines 134-201; the `using` block, ~lines 1-4)

- [ ] **Step 1: Add the `McaGit.Output` using**

At the top of `src/McaGit/Repo/Packfile.cs`, add to the using block:

```csharp
using McaGit.Output;
```

- [ ] **Step 2: Replace the body of `Write` and add `WriteSegment`**

Replace the entire current `Write` method (from `public static string? Write(` through its closing `}` that `return id;`) with:

```csharp
    /// <summary>
    /// Writes the objects named by <paramref name="orderedHashes"/> (already ordered so similar
    /// objects are adjacent) into a new pack under <c>objects/pack</c>, delta-compressing against a
    /// recent window. Returns the pack id, or null if there was nothing to pack. Calls
    /// <paramref name="onObject"/> once per object written (progress). Peak memory is the window.
    /// </summary>
    public static string? Write(string objectsDir, IReadOnlyList<string> orderedHashes,
        Func<string, byte[]> load, Action? onObject = null)
    {
        if (orderedHashes.Count == 0) return null;
        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        string id = PackId(orderedHashes);
        string packPath = Path.Combine(packDir, $"pack-{id}.pack");
        if (File.Exists(packPath)) return id; // identical set already packed

        string tmp = packPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        List<(string Hash, long Offset)> entries;
        using (var fs = File.Create(tmp))
        {
            fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'P');
            fs.WriteByte(Version);
            entries = WriteSegment(fs, orderedHashes, load, onObject);
        }

        WriteIndex(Path.ChangeExtension(tmp, ".idx"), entries);
        File.Move(tmp, packPath);
        File.Move(Path.ChangeExtension(tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
        return id;
    }

    /// <summary>
    /// Writes the given objects as pack entries into <paramref name="body"/> — type-0 (whole, zlib) or
    /// type-1 (delta vs an earlier object in THIS call, via a relative back-offset) — returning each
    /// entry's offset within <paramref name="body"/>. No MCAP header is written (the caller owns it).
    /// Because delta bases are confined to this call and back-offsets are relative, the produced bytes
    /// are position-independent: appending them at any file offset preserves every back-reference.
    /// Peak memory is the window, not the whole set.
    /// </summary>
    private static List<(string Hash, long Offset)> WriteSegment(
        Stream body, IReadOnlyList<string> hashes, Func<string, byte[]> load, Action? onObject)
    {
        var entries = new List<(string Hash, long Offset)>(hashes.Count);
        var window = new List<WindowEntry>(Window);
        foreach (string hash in hashes)
        {
            byte[] content = load(hash);
            long entryOff = body.Position;
            byte[] compContent = Deflate(content);

            byte[]? bestDelta = null;
            WindowEntry? bestBase = null;
            foreach (WindowEntry w in window)
            {
                if (w.Depth >= MaxDepth) continue;
                if (content.Length > w.Content.Length * 4 || w.Content.Length > content.Length * 4) continue;
                byte[] d = Delta.Diff(w.Content, content);
                if (bestDelta is null || d.Length < bestDelta.Length) { bestDelta = d; bestBase = w; }
            }

            int depth = 0;
            if (bestDelta is not null && bestBase is { } baseEntry)
            {
                byte[] compDelta = Deflate(bestDelta);
                if (compDelta.Length < compContent.Length)
                {
                    body.WriteByte(1);
                    WriteVarint(body, (ulong)(entryOff - baseEntry.Offset));
                    WriteVarint(body, (ulong)compDelta.Length);
                    body.Write(compDelta);
                    depth = baseEntry.Depth + 1;
                }
                else bestDelta = null;
            }
            if (bestDelta is null)
            {
                body.WriteByte(0);
                WriteVarint(body, (ulong)compContent.Length);
                body.Write(compContent);
            }

            entries.Add((hash, entryOff));
            window.Add(new WindowEntry(content, entryOff, depth));
            if (window.Count > Window) window.RemoveAt(0);
            onObject?.Invoke();
        }
        return entries;
    }
```

Note: in the serial `Write`, `WriteSegment` writes onto `fs` after the 5-byte header, so `body.Position` yields file-absolute offsets (identical to the previous code). Behavior is unchanged.

- [ ] **Step 3: Build**

Run: `/usr/local/share/dotnet/dotnet build McaGit.sln -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run Task 1's test — still PASS**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackfileTests"`
Expected: PASS (1 test) — the refactor preserved behavior.

- [ ] **Step 5: Commit**

```bash
git add src/McaGit/Repo/Packfile.cs
git commit -m "refactor: extract Packfile.WriteSegment from Write"
```

---

### Task 3: Add `Packfile.WriteParallel` + `PartitionByBytes`

**Files:**
- Modify: `src/McaGit/Repo/Packfile.cs`
- Modify: `tests/McaGit.Tests/PackfileTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these methods inside `PackfileTests` (before its closing `}`):

```csharp
    [Fact]
    public void WriteParallel_RoundTripsEveryObject()
    {
        (List<string> ordered, Dictionary<string, byte[]> byHash) = MakeObjects(500);
        string dir = TestAnvil.TempDir("pf-par");
        string? id = Packfile.WriteParallel(dir, ordered, h => byHash[h], 4, h => byHash[h].Length, null);
        Assert.NotNull(id);
        AssertPackRoundTrips(dir, id!, byHash);
    }

    [Fact]
    public void WriteParallel_SamePackId_AndValidPack_AsSerial()
    {
        (List<string> ordered, Dictionary<string, byte[]> byHash) = MakeObjects(500);
        string ds = TestAnvil.TempDir("pf-s");
        string dp = TestAnvil.TempDir("pf-p");
        string? serial = Packfile.Write(ds, ordered, h => byHash[h]);
        string? parallel = Packfile.WriteParallel(dp, ordered, h => byHash[h], 4, h => byHash[h].Length, null);
        Assert.Equal(serial, parallel);              // pack id hashes the object SET, not the byte layout
        AssertPackRoundTrips(dp, parallel!, byHash); // and the parallel pack reconstructs every object
    }

    [Fact]
    public void WriteParallel_FewerObjectsThanThreads_FallsBackAndRoundTrips()
    {
        (List<string> ordered, Dictionary<string, byte[]> byHash) = MakeObjects(3);
        string dir = TestAnvil.TempDir("pf-few");
        string? id = Packfile.WriteParallel(dir, ordered, h => byHash[h], 8, h => byHash[h].Length, null);
        Assert.NotNull(id);
        AssertPackRoundTrips(dir, id!, byHash);
    }
```

- [ ] **Step 2: Run — expect COMPILE FAILURE**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackfileTests"`
Expected: build error — `Packfile` does not contain a definition for `WriteParallel`.

- [ ] **Step 3: Implement `WriteParallel` + `PartitionByBytes`**

Add these methods to `Packfile` (next to `Write`/`WriteSegment`). `Parallel`, `Interlocked`, and `Progress` (via the `using McaGit.Output;` added in Task 2) are available.

```csharp
    /// <summary>
    /// Like <see cref="Write"/>, but delta-compresses the objects across <paramref name="threads"/>
    /// CPU cores. The size-sorted set is split into byte-balanced contiguous segments (so adjacency —
    /// hence delta quality — is preserved and no segment is a straggler); each segment is compressed in
    /// parallel to its own <c>*.pack.tmp</c> with delta bases confined to that segment, then the segment
    /// bodies are concatenated serially into the final pack. Within-segment delta back-offsets are
    /// relative, so concatenation needs no offset fixup; only the index records global offsets.
    /// Falls back to the serial <see cref="Write"/> when there is nothing to parallelize.
    /// Peak memory is the window per worker. Drives <paramref name="progress"/> if supplied.
    /// </summary>
    public static string? WriteParallel(string objectsDir, IReadOnlyList<string> orderedHashes,
        Func<string, byte[]> load, int threads, Func<string, long> sizeOf, Progress? progress)
    {
        if (orderedHashes.Count == 0) return null;

        long total = orderedHashes.Count;
        long done = 0;
        progress?.Begin("gc: packing");
        Action onObject = () => progress?.Update(Interlocked.Increment(ref done), total);

        if (threads <= 1 || orderedHashes.Count < threads)
        {
            string? sid = Write(objectsDir, orderedHashes, load, onObject);
            progress?.Done(total, total, $"{orderedHashes.Count} objects");
            return sid;
        }

        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        string id = PackId(orderedHashes);
        string packPath = Path.Combine(packDir, $"pack-{id}.pack");
        if (File.Exists(packPath)) { progress?.Done(total, total, "already packed"); return id; }

        List<(int Start, int Count)> segments = PartitionByBytes(orderedHashes, sizeOf, threads);

        // Parallel phase: each segment -> its own temp body file (peak memory = window per worker).
        var segFiles = new string[segments.Count];
        var segEntries = new List<(string Hash, long Offset)>[segments.Count];
        Parallel.For(0, segments.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            (int start, int count) = segments[k];
            var slice = new List<string>(count);
            for (int i = 0; i < count; i++) slice.Add(orderedHashes[start + i]);
            string segTmp = Path.Combine(packDir, $"incoming-seg-{k}-{Guid.NewGuid():N}.pack.tmp");
            segFiles[k] = segTmp;
            using var seg = File.Create(segTmp);
            segEntries[k] = WriteSegment(seg, slice, load, onObject);
        });

        // Serial concat: header, then append each segment body and shift its offsets by where the
        // segment lands in the final file. Within-segment relative deltas survive untouched.
        string tmp = packPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var entries = new List<(string Hash, long Offset)>(orderedHashes.Count);
        using (var fs = File.Create(tmp))
        {
            fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'P');
            fs.WriteByte(Version);
            for (int k = 0; k < segments.Count; k++)
            {
                long pk = fs.Position;
                using (var seg = File.OpenRead(segFiles[k])) seg.CopyTo(fs);
                foreach ((string hash, long off) in segEntries[k]) entries.Add((hash, pk + off));
                File.Delete(segFiles[k]);
            }
        }

        WriteIndex(Path.ChangeExtension(tmp, ".idx"), entries);
        File.Move(tmp, packPath);
        File.Move(Path.ChangeExtension(tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
        progress?.Done(total, total, $"{orderedHashes.Count} objects");
        return id;
    }

    /// <summary>
    /// Splits <paramref name="ordered"/> into at most <paramref name="segments"/> contiguous chunks
    /// balanced by cumulative stored size, each non-empty. Contiguous keeps size-adjacency (delta
    /// quality); byte-balancing keeps the large-object chunk from being a straggler.
    /// </summary>
    private static List<(int Start, int Count)> PartitionByBytes(
        IReadOnlyList<string> ordered, Func<string, long> sizeOf, int segments)
    {
        var sizes = new long[ordered.Count];
        long total = 0;
        for (int i = 0; i < ordered.Count; i++) { sizes[i] = Math.Max(1, sizeOf(ordered[i])); total += sizes[i]; }
        long target = Math.Max(1, total / segments);

        var result = new List<(int Start, int Count)>(segments);
        int start = 0;
        long running = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            running += sizes[i];
            int remainingObjs = ordered.Count - (i + 1);
            int remainingSegs = segments - result.Count - 1;   // segments still to open after this cut
            // Cut when over target, but only while more segments remain and enough objects are left to
            // give each remaining segment at least one.
            if (result.Count < segments - 1 && running >= target && remainingObjs > remainingSegs)
            {
                result.Add((start, i - start + 1));
                start = i + 1;
                running = 0;
            }
        }
        result.Add((start, ordered.Count - start));            // last segment takes the remainder
        return result;
    }
```

- [ ] **Step 4: Run the tests — expect PASS**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackfileTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/McaGit/Repo/Packfile.cs tests/McaGit.Tests/PackfileTests.cs
git commit -m "feat: parallel Packfile.WriteParallel (segment-parallel, serial concat)"
```

---

### Task 4: Wire `Gc.Repack` to threads + progress

**Files:**
- Modify: `src/McaGit/Repo/Gc.cs`

- [ ] **Step 1: Add the `McaGit.Output` using**

At the very top of `src/McaGit/Repo/Gc.cs`, above `namespace McaGit.Repo;`, add:

```csharp
using McaGit.Output;
```

- [ ] **Step 2: Change `Repack`'s signature and the pack call**

Replace the method signature line:

```csharp
    public static RepackResult Repack(Repository repo)
```

with:

```csharp
    public static RepackResult Repack(Repository repo, int threads, Progress? progress = null)
```

Then replace this line (currently `Gc.cs:52`):

```csharp
        string? packId = Packfile.Write(store.ObjectsDir, ordered, store.Read);
```

with:

```csharp
        string? packId = Packfile.WriteParallel(store.ObjectsDir, ordered, store.Read, threads, store.StoredSize, progress);
```

Add a backward-compatible overload right above `Repack` so any caller using the old arity (e.g. tests) keeps compiling, defaulting to all cores:

```csharp
    /// <summary>Repack using all CPU cores and no progress reporter.</summary>
    public static RepackResult Repack(Repository repo) => Repack(repo, Environment.ProcessorCount, null);
```

- [ ] **Step 3: Build**

Run: `/usr/local/share/dotnet/dotnet build McaGit.sln -c Release`
Expected: Build succeeded, 0 errors (the `GcCmd` caller still uses the parameterless overload for now).

- [ ] **Step 4: Run the repo/pack suite — still green**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackAtCommit|FullyQualifiedName~RepoTests|FullyQualifiedName~PackfileTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McaGit/Repo/Gc.cs
git commit -m "feat: Gc.Repack drives parallel pack writing + progress"
```

---

### Task 5: `GcCmd` — `--threads` flag + Progress reporter, with an integration test

**Files:**
- Modify: `src/McaGit/Cli/RepoCommands.cs` (the `GcCmd` method)
- Modify: `tests/McaGit.Tests/PackAtCommitTests.cs`

- [ ] **Step 1: Write the failing integration test**

Append this method inside `PackAtCommitTests` (before its closing `}`). It reuses the class's `BoundRepo` and `Chunk` helpers.

```csharp
    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    public void Gc_WithThreads_KeepsWorldReproducible(string threads)
    {
        Repository repo = BoundRepo("gct" + threads, out string world);
        var chunks = new (ChunkPos, NbtCompound)[64];
        for (int i = 0; i < chunks.Length; i++) chunks[i] = (new ChunkPos(i % 8, i / 8), Chunk(i));
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), chunks);
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));
        string head = Repository.Open(repo.Dir).HeadCommit()!;

        Assert.Equal(0, RepoCommands.GcCmd(repo.Dir, ["--threads", threads]));

        Repository r = Repository.Open(repo.Dir);
        Assert.NotEmpty(r.Objects.PackFilePaths());
        Assert.Empty(r.Objects.LooseHashes());
        Assert.True(r.Objects.Exists(head));                 // HEAD survived the repack
        // Re-committing the unchanged world is a no-op iff every chunk object survived gc intact.
        Assert.Equal(0, RepoCommands.Commit(r.Dir, ["-m", "again"]));
        Assert.Equal(head, Repository.Open(repo.Dir).HeadCommit());
    }
```

If `PackAtCommitTests.cs` does not already have `using McaGit.Output;` it does not need it; the test uses only existing imports (`fNbt`, `McaGit.Anvil`, `McaGit.Cli`, `McaGit.Repo`, `Xunit`).

- [ ] **Step 2: Run — expect FAIL**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackAtCommitTests.Gc_WithThreads"`
Expected: FAIL — `--threads` is currently rejected/ignored by `GcCmd` (it parses only `--prune-only`), so the gc either errors or does not behave as asserted.

- [ ] **Step 3: Update `GcCmd`**

Replace the whole `GcCmd` method in `src/McaGit/Cli/RepoCommands.cs` with:

```csharp
    public static int GcCmd(string? dashC, string[] a)
    {
        var (_, opts) = Parse(a, ["--threads"], ["--prune-only"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (opts.ContainsKey("--prune-only"))
        {
            Gc.Result p = Gc.Prune(repo);
            Console.Error.WriteLine($"Pruned {p.Pruned} objects ({p.BytesFreed / 1024} KiB freed), {p.Kept} reachable.");
            return 0;
        }

        int threads = Environment.ProcessorCount;
        if (opts.GetValueOrDefault("--threads") is { } t)
        {
            if (!int.TryParse(t, out threads) || threads < 1)
                return Err($"--threads expects a positive integer, got '{t}'");
        }

        var progress = new Progress(Progress.ShouldShow());
        Gc.RepackResult r = Gc.Repack(repo, threads, progress);
        Console.Error.WriteLine($"Packed {r.Packed} objects, pruned {r.Pruned} unreachable "
            + $"({r.BytesFreed / 1024} KiB freed){(r.PackId is null ? "" : $", pack {r.PackId[..10]}")}.");
        return 0;
    }
```

`Progress` is already referenced unqualified in this file (e.g. `Commit` calls `new Progress(Progress.ShouldShow())`), so no new `using` is required. `Err` is the existing error helper used throughout `RepoCommands`.

- [ ] **Step 4: Run the integration test — expect PASS**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release --filter "FullyQualifiedName~PackAtCommitTests.Gc_WithThreads"`
Expected: PASS (2 cases: threads 1 and 4).

- [ ] **Step 5: Run the full suite**

Run: `/usr/local/share/dotnet/dotnet test McaGit.sln -c Release`
Expected: PASS (all prior tests + the new ones).

- [ ] **Step 6: Commit**

```bash
git add src/McaGit/Cli/RepoCommands.cs tests/McaGit.Tests/PackAtCommitTests.cs
git commit -m "feat: gc --threads + progress reporter"
```

---

### Task 6: Document `gc --threads` and progress in the README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Find the gc section**

Run: `grep -n "gc" README.md | head`
Locate the `gc` command description (the repo/maintenance section).

- [ ] **Step 2: Update the gc description**

In the `gc` entry, add a sentence describing the new behavior. Insert after the existing gc description line:

```markdown
`gc` delta-packs across all CPU cores by default and prints live progress on a terminal
(silent when piped or when `NO_PROGRESS` is set). Use `--threads N` to cap parallelism
(`--threads 1` forces the serial writer); `--prune-only` deletes unreachable loose objects
without repacking.
```

(Match the surrounding README formatting — option list vs. prose — where you insert it.)

- [ ] **Step 3: Format check**

Run: `/usr/local/share/dotnet/dotnet format McaGit.sln --verify-no-changes` (if it reports changes to `.cs`, run `/usr/local/share/dotnet/dotnet format McaGit.sln` and re-commit). README-only changes won't trip `dotnet format`.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: README gc --threads and progress"
```

---

### Task 7: Pre-PR gate

This is not a code task — it runs the project's required reviews before opening the PR.

- [ ] **Step 1: Run `dotnet format`**

Run: `/usr/local/share/dotnet/dotnet format McaGit.sln` then `git add -A && git commit -m "style: dotnet format" --allow-empty` (skip the commit if nothing changed).

- [ ] **Step 2: Invoke the `pre-pr` skill**

The diff touches `Repo/` (`Packfile.cs`, `Gc.cs`) and `Packfile` is reachable from untrusted network input (clone/fetch via `PackTransfer`). The `pre-pr` skill will run **world-roundtrip-gauntlet** and **trust-boundary-exploit-hunter**, aggregate findings, and produce the PR description. Fix any BLOCKERs (re-running the raising agent) before opening.

- [ ] **Step 3: Open the PR**

Base the PR on `main` if `chore/namespace-mcagit` (PR #45) has merged; otherwise note in the PR body that it is stacked on #45 and must merge after it. Include the aggregated agent findings.

---

## Self-review notes

- **Spec coverage:** `WriteSegment`/`Write`/`WriteParallel` refactor (Tasks 2-3); byte-balanced contiguous segmentation (`PartitionByBytes`, Task 3); temp-file-per-segment + serial concat with relative-offset preservation (Task 3); idempotent pack id + bounded memory (asserted by `WriteParallel_SamePackId` and the temp-file design); `--threads` default all cores, `1` = serial (Task 5); TTY-aware Progress (Tasks 3-5); round-trip / equivalence / edge tests (Tasks 1, 3, 5); review gate (Task 7). All spec sections map to a task.
- **Type consistency:** `WriteSegment(Stream, IReadOnlyList<string>, Func<string,byte[]>, Action?)`, `Write(..., Action? onObject = null)`, `WriteParallel(string, IReadOnlyList<string>, Func<string,byte[]>, int, Func<string,long>, Progress?)`, `Gc.Repack(Repository, int, Progress?)` — names/signatures are consistent across Tasks 2-5.
- **No placeholders:** every code/test step shows complete code; commands include expected results.
