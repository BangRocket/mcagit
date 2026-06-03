using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McaDiff.Diff;

namespace McaDiff.Patch;

/// <summary>
/// One node change: at <see cref="Path"/>, the value is <see cref="Base"/> before
/// and <see cref="Value"/> after (either may be null = absent). Forward apply
/// requires current == Base then writes Value; reverse swaps them. Values are
/// <c>NbtJson</c>-encoded. A <see cref="Path"/> of "" addresses the whole unit
/// (chunk/loose-file) root.
/// </summary>
public sealed class PatchOp
{
    public string Path { get; set; } = "";
    public JsonNode? Base { get; set; }
    public JsonNode? Value { get; set; }

    [JsonIgnore] public bool IsRoot => Path.Length == 0;
}

/// <summary>Per-chunk ops within a region patch entry.</summary>
public sealed class ChunkPatch
{
    public int X { get; set; }
    public int Z { get; set; }
    public DiffStatus Status { get; set; }
    public int Timestamp { get; set; }
    public List<PatchOp> Ops { get; set; } = [];
}

/// <summary>A patched file: region (with per-chunk ops) or loose NBT (with node ops).</summary>
public sealed class PatchFileEntry
{
    public string Path { get; set; } = "";
    public UnitKind Kind { get; set; }
    public DiffStatus Status { get; set; }
    public List<PatchOp>? Ops { get; set; }       // loose files
    public List<ChunkPatch>? Chunks { get; set; } // region files
}

/// <summary>A portable, bidirectional world patch (the <c>*.mcapatch</c> document).</summary>
public sealed class WorldPatch
{
    public int Version { get; set; } = 1;
    public string? Base { get; set; }
    public string? Target { get; set; }
    public string? Note { get; set; }
    public List<PatchFileEntry> Files { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WorldPatch FromJson(string json)
        => JsonSerializer.Deserialize<WorldPatch>(json, JsonOptions)
           ?? throw new FormatException("Empty or invalid patch file.");
}
