---
name: production-source-map
description: Maps src/ files to their test coverage, highlighting untested code paths and risky gaps
metadata:
  type: reference
---

## Fully Untested Production Files
- `src/McaGit/Repo/StatusCalc.cs` — no test at all
- `src/McaGit/Nbt/NbtJson.cs` — tested only indirectly via NbtCodecTests; `ToJson`/`FromJson` edge cases (null, empty compound, etc.) not directly tested
- `src/McaGit/Output/Ansi.cs` — zero tests
- `src/McaGit/Output/JsonDiffFormatter.cs` — zero tests
- `src/McaGit/Output/TextDiffFormatter.cs` — zero tests
- `src/McaGit/Cli/CliCommon.cs` — zero tests
- `src/McaGit/Cli/RepoCommands.cs` — tested only via `RepoCommands.Commit` and `RepoCommands.Clean` in Tier4/Tier5; most commands untested
- `src/McaGit/Repo/SshTransport.cs` — zero tests (SSH transport, distinct from signing)
- `src/McaGit/Repo/RepoDiffer.cs` — tested in RepoTests but only the happy path

## Major Untested Code Paths (Within Otherwise-Tested Files)

### ChunkCodec.cs
- `ChunkCompression.Lz4` → `UnsupportedChunkException` path — never exercised
- `ChunkCompression.None` decode path — never exercised
- `ChunkCompression.Custom` → `NotSupportedException` in Encode — never exercised

### RegionFile.cs
- `external = true` path (oversized `.mcc` chunk) — never exercised
- truncated-file / < 8 KiB path returning empty dict — never exercised
- `ParseRegionCoords` with non-standard filename (returns 0,0) — never exercised
- `CountChunks` static method — never exercised

### Packfile.cs
- Delta chain depth (entries requiring multi-hop delta reconstruction) — not tested
- Corrupted `.pack` / `.idx` files (magic mismatch, truncation) — not tested
- `ResolvePrefix` ambiguous match within a pack — not tested

### ObjectStore.cs
- `ImportRaw` with hash mismatch → `InvalidDataException` — not tested
- Concurrent write race (the `File.Move` + `IOException` path) — not tested
- `ResolvePrefix` ambiguous across loose + pack — not tested

### SshSigner.cs
- `Sign` with missing key file → `FileNotFoundException` — not tested
- `Verify` with `allowedSignersFile` that exists and contains the principal — not tested
- `available = false` branch of `Verify` — not tested

### Merger.cs / MergeManifests
- Delete/modify conflict on regions — not tested
- Binary blob conflict (both sides changed a `.dat`) — not tested
- Three-way merge where base is null (both sides added same file) — not tested

### Stash.cs
- `Apply` with conflict (stash conflicts with current worktree) — not tested
- `Drop` method — not tested
- `Clear` method — not tested
- `Apply` with `pop: false` — not tested

### Rebase.cs
- Fast-forward case of rebase — not tested
- Multi-commit replay with conflicts — not tested
- `--onto` with conflict — not tested

### IgnoreRules.cs
- `?` glob character — not tested
- Negation patterns (gitignore `!`) — not present in production code, so correctly not tested

### RemoteOps / Network
- Fetch all branches (no branch filter) — not tested
- Force push — not tested
- `ImportRaw` with bad hash (poisoned object from server) — not tested
- HTTP 404 on GetObject — not tested

### PatchApplier.cs
- `OnlyCategories` filter — not tested
- Whole-file add via patch (adding a new `.dat` file) — not tested
- Whole-file remove via patch (removing a `.dat` file) — not tested
- Empty region add/remove edge case — not tested

### StatusCalc.cs
- Entirely untested — no tests exist for `StatusCalc.Compute`
