# Contributing to mcadiff

Thanks for helping. mcadiff is a single .NET 10 (LTS) console app (`src/McaDiff`, assembly `mcadiff`) plus an xUnit test project. Runtime dependencies: `fNbt`, `K4os.Compression.LZ4`, and `Azure.Storage.Blobs` / `AWSSDK.S3` (cloud remotes).

## Build and test

```sh
dotnet build -c Release
dotnet test                                            # full suite (all synthetic — no fixtures)
dotnet test --filter "FullyQualifiedName~NbtComparer"  # one class
dotnet test --filter "DisplayName~merge"               # by name fragment
dotnet run --project src/McaDiff -- <args>             # run the CLI
```

Targets `net10.0`; install the .NET 10 SDK.

## Before you push

1. `dotnet test` — the full suite must be green locally.
2. `dotnet format McaGit.sln` — CI runs `--verify-no-changes` as a gate. The formatter expands compact multi-member object initializers to one per line and indents `case: {}` block bodies a level deeper; an `.editorconfig` pins these so editors agree.
3. If you changed user-facing behavior, update `README.md` (and the relevant `docs/` spec).

## Writing tests

Tests build synthetic worlds and regions through `tests/McaDiff.Tests/TestAnvil.cs` — there are **no binary fixtures**. Use `TestAnvil.Root`, `TestAnvil.WriteRegion`, `TestAnvil.WriteLoose`, and `TestAnvil.TempDir` to construct inputs. See [`TESTING.md`](TESTING.md) for the test-type taxonomy and which kind to reach for.

Recent work follows a tiered naming convention (`GitLikeTierNTests.cs`, and per-feature files like `BucketTransportTests.cs`).

## Architecture in one paragraph

Three layers share one core: **diff** (display), **patch** (extract / apply), and **repo** (VCS) all sit on the same NBT comparison walk (`Diff/NbtComparer` + `IDiffSink`) and canonical encoding (`Nbt/NbtCanonical`). Any change to diff semantics must go through the comparer / sink so display and patch cannot drift. The region container lives in `Anvil/`, the NBT semantics in `Nbt/`, and the VCS in `Repo/`. The format specs are in [`docs/repo-format.md`](docs/repo-format.md) and [`docs/mcapatch-format.md`](docs/mcapatch-format.md); the load-bearing invariants are in [`.github/copilot-instructions.md`](.github/copilot-instructions.md).

## Invariants you must preserve

- `commit` → `checkout` reproduces a world faithfully (playable in Minecraft); tests assert exact reproduction.
- The canonical NBT encoding is deterministic and version-independent — never make `NbtCanonical` depend on fNbt internals, ordering, or a diff-path normalization.
- Diff-only normalizations (e.g. dropping a redundant single-entry-palette `data`) belong in the diff path, never on the canonical / storage path.
- `diff` / `extract` / `status` must never modify a world.

## Pull requests

CI re-runs build + tests on Ubuntu and Windows, coverage, an end-to-end round-trip gauntlet against the sample worlds, `dotnet format`, markdownlint, and CodeQL. Keep them green. Describe what you changed and why; if you touched diff / patch / repo semantics, note how you verified the round-trip still holds.

## License

By contributing you agree your contributions are licensed under the project's [GPL-3.0](LICENSE).
