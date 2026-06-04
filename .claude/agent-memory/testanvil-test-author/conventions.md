---
name: test-suite-conventions
description: xUnit framework, tier grouping, assertion style, naming patterns, and house-style rules for McaDiff tests
metadata:
  type: reference
---

## Framework
- **xUnit** with `[Fact]` and `[Theory]`/`[InlineData]`. No NUnit, no MSTest.
- Namespace: `McaDiff.Tests` (all test files).
- No `IClassFixture` or shared state — every test constructs its own world via `TestAnvil.TempDir("label")`.

## Tier Grouping
- `GitLikeTier1Tests` — config/identity, committer, annotated/SSH-signed tags, object classification, fsck
- `GitLikeTier2Tests` — delta codec, packfiles, gc repack
- `GitLikeTier3Tests` — recursive merge base, conflict stop/continue/abort workflow
- `GitLikeTier4Tests` — bisect binary search, staging index
- `GitLikeTier5Tests` — HEAD@{n} reflog syntax, stash, rebase, clean, hooks
- Other test classes (not tier-named): `NbtComparerTests`, `NbtCodecTests`, `NbtPathTests`, `ListMatcherTests`, `RegionFileTests`, `PatchTests`, `WorldDifferTests`, `RepoTests`, `NetworkTests`

## Assertion Style
- Dominant pattern: build synthetic world → drive system under test → assert `WorldDiffer.Diff(outDir, expected, ...).HasDifferences == false` (exact-reproduction equality).
- Supplemented by direct property assertions on `CommitObject`, `Manifest`, `NbtChange`, etc.
- `NbtEquality.DeepEquals(a, b)` for NBT tree equality.
- `Assert.Empty`, `Assert.Single`, `Assert.Contains`, `Assert.DoesNotContain`, `Assert.Equal`, `Assert.True`/`False` from xUnit.

## Naming
- Method names are descriptive behavior statements: `Merge_Conflict_StopsWithoutCommitting_AndRecordsState`
- Private helpers at bottom of class: `World()`, `CommitWorld()`, `AB()`, `WriteA()`, `ChunkAB()` etc.
- Label strings in `TempDir` match the test intent: `"cf"` = conflict, `"rpt"` = repack-tags, etc.

## House-Style Rules
- No fixture files — all state built programmatically through TestAnvil.
- No mocking of the object store or repository.
- `TestAnvil.TempDir(label)` for isolated temp dirs — each test gets its own.
- SSH-signing tests guard on `SshSigner.Available` and silently return if unavailable.
- "Real region file" test in RegionFileTests silently skips if env var `MCADIFF_TEST_REGION` is unset (soft skip, not `[Skip]`).
