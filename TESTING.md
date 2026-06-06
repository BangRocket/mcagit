# Testing guide

All tests are synthetic — there are **no binary fixtures**. Inputs are built in-memory through `tests/McaDiff.Tests/TestAnvil.cs` and written to temp dirs. This keeps the suite fast, deterministic, and reviewable. When adding a test, pick the narrowest type that proves the behavior.

## Test types

### Unit — NBT and core logic

Exercise one class with hand-built `NbtTag` trees. No region files, no repo. Reach for this for `NbtComparer`, `NbtIdentity`, `NbtPath`, `NbtJson`, `NbtCanonical`, `ObjectStore`, `Merger`, formatters.

Example: build two `NbtCompound`s and assert the list of `NbtChange`s. See `NbtComparerTests`, `OutputFormatterTests` (synthetic `WorldDiff`, no TestAnvil).

### Wire-format — the bytes on disk

Round-trip through the real container or encoding and assert the bytes / decoded tree survive. Reach for this for `RegionWriter`, `ChunkCodec` (incl. LZ4), `Packfile` / `Delta`, `NbtCanonical` determinism, `NbtJson` losslessness (especially longs beyond 2^53).

Example: write a region with `TestAnvil.WriteRegion`, read it back, assert chunk identity. See `RegionFileTests`, `PackfileTests`.

### Integration — patch apply and repo ops

Drive a multi-step flow against synthetic worlds: extract a patch and apply it, or commit and check out. Assert the result reproduces the expected world via `WorldDiffer.Diff(...).HasDifferences == false`.

Example: `extract` old→new, `apply` onto old, diff against new = clean. See the patch tests and `RepoTests`.

### End-to-end — the round-trip gauntlet

The CI `roundtrip` job runs the published binary against the real sample worlds in `compare-worlds/` (`New_World_Older` / `New_World_Newer`): self-diff clean, cross-diff finds changes, forward / reverse patch round-trips, commit → checkout reproduces, `gc` + `fsck` stay intact. Use the stable branch refs `main` / `main~1`, never `HEAD~1` (it detaches HEAD and later refs drift).

### Invariant — the properties that must always hold

Guard the load-bearing properties directly:

- `commit` → `checkout` reproduces a world exactly.
- The canonical encoding is deterministic regardless of on-disk compression.
- Identity-based list matching survives reorders.
- A diff-only normalization never changes a stored object's hash.

Example: `SingleEntryPaletteTests` asserts a diff-path normalization does not diff (and a real change still does); round-trip tests assert exact reproduction.

## Conventions

- Build inputs with `TestAnvil.Root` / `WriteRegion` / `WriteLoose`; allocate scratch dirs with `TestAnvil.TempDir`.
- Name files by feature or tier (`GitLikeTierNTests.cs`, `BucketTransportTests.cs`).
- Prefer deterministic concurrency primitives (`Barrier`, `ManualResetEventSlim`) over sleeps — see `RepoLockTests.ConcurrentAcquire_ExactlyOneWins`.
- `Assert.Skip` does **not** exist in xUnit 2.9.3. A test that depends on an unavailable environment (a real region file via `MCAGIT_TEST_REGION`, or `ssh-keygen`) returns early; be aware this shows green when skipped.

## Running

```sh
dotnet test                                            # everything
dotnet test --filter "FullyQualifiedName~Packfile"     # one class
dotnet test --filter "DisplayName~merge"               # by name fragment
MCAGIT_TEST_REGION=/path/to/r.0.0.mca dotnet test     # enable the real-region parse test
```
