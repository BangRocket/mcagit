---
name: testanvil-api
description: TestAnvil.cs builder methods, factory helpers, and idiomatic synthetic-world composition patterns
metadata:
  type: reference
---

## TestAnvil Static Methods (C:\Users\steven.cady\repos\personal\mcagit\tests\McaGit.Tests\TestAnvil.cs)

| Method | Signature | Notes |
|--------|-----------|-------|
| `Root` | `static NbtCompound Root(params NbtTag[] tags)` | Creates root compound named `""`. Primary factory for synthetic chunks. |
| `BlockEntity` | `static NbtCompound BlockEntity(string id, int x, int y, int z, params NbtTag[] extra)` | Creates a block entity compound with id/x/y/z fields. |
| `DeepClone` | `static NbtCompound DeepClone(NbtCompound c)` | Wraps `c.Clone()`. |
| `WriteSingleChunkRegion` | `static void WriteSingleChunkRegion(string path, ChunkPos pos, NbtCompound root)` | Writes real Anvil region file with one ZLib-compressed chunk. |
| `WriteRegion` | `static void WriteRegion(string path, params (ChunkPos Pos, NbtCompound Root)[] chunks)` | Writes region via real RegionWriter with multiple chunks. |
| `WriteLoose` | `static void WriteLoose(string path, NbtCompound root)` | Writes GZip NBT file (level.dat, playerdata, etc.) via ChunkCodec.SaveNbtFile. |
| `TempDir` | `static string TempDir(string label)` | Creates unique temp dir at `%TEMP%/mcagit-test-{label}-{guid}`. Returns path. |

## Idiomatic Composition Patterns

```csharp
// Minimal chunk world
private static string World(string tag) {
    string dir = TestAnvil.TempDir("w-" + tag);
    TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"),
        (new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtString("Status", tag))));
    return dir;
}

// Chunk with multiple fields (a, b integers used in merge tests)
private static NbtCompound AB(int a, int b) =>
    TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a), new NbtInt("b", b));

// CommitWorld helper (used in almost every tier)
private static string CommitWorld(Repository repo, string worldDir, string msg) {
    Manifest m = Snapshotter.Snapshot(repo, worldDir);
    string? head = repo.HeadCommit();
    return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
}

// World with level.dat
TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), 
    TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", 5) }));

// Level.dat helper (used in PatchTests and WorldDifferTests)
private static NbtCompound Level(long time) =>
    TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", time), new NbtString("LevelName", "T") });
```

## Gaps in TestAnvil
- No `WriteLz4ChunkRegion` builder (LZ4 is a production code path with zero test coverage).
- No `WriteExternalChunk` builder (oversized/external `.mcc` chunks, production code reads them).
- No `WriteCorruptRegion` builder for header-level corruption tests.
- No multi-region world builder (tests only use `r.0.0.mca`).
