---
name: gauntlet-invocations
description: Exact working command invocations for all gauntlet checks (extract, apply, apply --reverse, diff, commit, checkout, gc, fsck) against compare-worlds/ fixtures
metadata:
  type: project
---

# Gauntlet Invocations

## Environment Setup

### Windows (steven.cady machine)
- SDK 10 installed, no .NET 9 runtime on this machine (uses 10.0.201).
- Use `$env:DOTNET_ROLL_FORWARD = "LatestMajor"` in PowerShell before any dotnet command.
- Or use `dotnet run --project src/McaDiff -c Release --no-build -- <args>` after building.
- Build: `dotnet build -c Release` from repo root.

### macOS / zsh (heidornj machine, /Volumes/Storage/Code/minecraft/mca-git)
- Default `dotnet` on PATH is SDK 9 and CANNOT target net10.0. Use the SDK-10 binary
  explicitly: `/usr/local/share/dotnet/dotnet` (verified 10.0.300).
- Built CLI (assembly renamed mcadiff→mcagit 2026-06-06): `src/McaDiff/bin/Release/net10.0/mcagit.dll`.
- CRITICAL zsh gotcha: a multi-word var like `MCAGIT="/usr/.../dotnet /path/mcagit.dll"`
  used as a command is NOT word-split → "no such file or directory" exit 127. Define a
  function instead:
  ```sh
  mcagit() { /usr/local/share/dotnet/dotnet /Volumes/Storage/Code/minecraft/mca-git/src/McaDiff/bin/Release/net10.0/mcagit.dll "$@"; }
  ```
- Agent bash cwd resets between calls → use absolute paths everywhere; stash the scratch
  path in /tmp/mcagit-scratch-path.txt and re-read it each call.
- Scratch: `SCRATCH=/tmp/mcagit-gauntlet-$(date +%Y%m%d-%H%M%S)`; cp -R the two
  compare-worlds/ fixtures in; clean up with `rm -rf` at the end.
- Byte spot-check raw blobs (e.g. icon.png) with `shasum -a 256` — expected byte-identical
  after checkout (corroborates semantic "No differences").

## Scratch Setup (never mutate compare-worlds/)
```powershell
$scratch = "C:\Temp\mcagit-gauntlet-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Force $scratch
Copy-Item -Recurse "compare-worlds\New_World_Older" "$scratch\New_World_Older"
Copy-Item -Recurse "compare-worlds\New_World_Newer" "$scratch\New_World_Newer"
```

## CLI Pattern
```powershell
$cli = "C:\Users\steven.cady\repos\personal\mcagit\src\McaDiff"
dotnet run --project $cli -c Release --no-build -- <args>
```

## Invariant 1: Extract → Apply → Diff (forward)
```powershell
# Extract
dotnet run --project $cli -c Release --no-build -- extract "$scratch\New_World_Older" "$scratch\New_World_Newer" -o "$scratch\forward.mcapatch"
# Apply
dotnet run --project $cli -c Release --no-build -- apply "$scratch\forward.mcapatch" "$scratch\New_World_Older" -o "$scratch\Old_Updated"
# Diff (expect exit 0, "No differences")
dotnet run --project $cli -c Release --no-build -- "$scratch\Old_Updated" "$scratch\New_World_Newer"
```

## Invariant 2: Apply --reverse
```powershell
dotnet run --project $cli -c Release --no-build -- apply --reverse "$scratch\forward.mcapatch" "$scratch\New_World_Newer" -o "$scratch\Newer_Restored"
dotnet run --project $cli -c Release --no-build -- "$scratch\Newer_Restored" "$scratch\New_World_Older"
# Expect exit 0, "No differences"
```

## Invariant 3: Commit → Checkout
```powershell
$repo = "$scratch\test-repo"
dotnet run --project $cli -c Release --no-build -- init $repo --worktree "$scratch\New_World_Older"
dotnet run --project $cli -c Release --no-build -- -C $repo commit -m "older world"
dotnet run --project $cli -c Release --no-build -- -C $repo config worktree "$scratch\New_World_Newer"
dotnet run --project $cli -c Release --no-build -- -C $repo commit -m "newer world"
dotnet run --project $cli -c Release --no-build -- -C $repo checkout main "$scratch\checkout_newer"
dotnet run --project $cli -c Release --no-build -- "$scratch\checkout_newer" "$scratch\New_World_Newer"
# Also checkout older via HEAD~1 ref
```

## Invariant 4: GC → Fsck → Checkout
```powershell
dotnet run --project $cli -c Release --no-build -- -C $repo gc
dotnet run --project $cli -c Release --no-build -- -C $repo fsck
dotnet run --project $cli -c Release --no-build -- -C $repo checkout main "$scratch\gc_checkout_newer"
dotnet run --project $cli -c Release --no-build -- "$scratch\gc_checkout_newer" "$scratch\New_World_Newer"
# Expect exit 0, "No differences"
```

## Observed Timings (SDK 10, Windows, Release build, 2026-06-03)
- Build: ~6s
- Extract Older→Newer: ~4.6s (13 files, 4707 ops, exit 0)
- Apply forward: ~3.5s (4711 ops, 0 conflicts, exit 0)
- Diff applied_forward vs Newer: ~2.1s (No differences, exit 0)
- Apply --reverse: ~2.9s (4711 ops, 0 conflicts, exit 0)
- Diff applied_reverse vs Older: ~1.8s (No differences, exit 0)
- Commit Older (cold): ~23s (32 files, 2601 chunks, exit 0)
- Commit Newer (warm): ~28s (36 files, 2652 chunks, exit 0)
- Checkout (from packed): ~3-3.5s
- Diff checkout vs original (semantic): ~2-6s (No differences, exit 0)
- GC (repack): ~10s (packed 4389 objects)
- Fsck after GC: ~3s (0 corrupt, 0 missing, exit 0)
- Test suite (122 tests): ~8s

## Fixture Sizes
- New_World_Older: 33 files (4 region/.mca, 4 entities/.mca empty/0-byte, various .dat, blobs)
- New_World_Newer: 37 files (same + 4 poi/ region files, no DIM-1/DIM1 region files)
- Overworld region files: 4 per world (r.-1.-1, r.-1.0, r.0.-1, r.0.0), all ZLib compression type 2
- All 2601 chunks (Older) and 2652 chunks (Newer) decode as ZLib (compression type 2)
- 0-byte entity .mca files are present in Older (valid Minecraft format)
