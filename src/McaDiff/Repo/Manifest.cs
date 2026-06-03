using System.Text.Json;
using System.Text.Json.Serialization;

namespace McaDiff.Repo;

/// <summary>
/// A whole-world snapshot by content hash (≈ a git tree). Region files map each
/// present chunk position ("x,z") to its chunk-object hash; loose NBT and all
/// other files map their relative path to a single object hash.
/// </summary>
public sealed class Manifest
{
    public SortedDictionary<string, SortedDictionary<string, string>> Regions { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Nbt { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Blobs { get; set; } = new(StringComparer.Ordinal);

    public string ToJson() => JsonSerializer.Serialize(this, RepoJson.Options);
    public static Manifest FromJson(string json) =>
        JsonSerializer.Deserialize<Manifest>(json, RepoJson.Options) ?? new Manifest();
}

/// <summary>A commit: a snapshot (<see cref="Tree"/>) plus history and metadata.</summary>
public sealed class CommitObject
{
    public string Tree { get; set; } = "";
    public List<string> Parents { get; set; } = [];
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string Time { get; set; } = ""; // ISO-8601

    public string ToJson() => JsonSerializer.Serialize(this, RepoJson.Options);
    public static CommitObject FromJson(string json) =>
        JsonSerializer.Deserialize<CommitObject>(json, RepoJson.Options)
        ?? throw new FormatException("invalid commit object");
}

internal static class RepoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
