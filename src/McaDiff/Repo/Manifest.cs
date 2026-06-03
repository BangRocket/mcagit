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

    /// <summary>Directories with no files — recorded so checkout reproduces them (git tracks none).</summary>
    public List<string> EmptyDirs { get; set; } = [];

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
    public string Time { get; set; } = ""; // ISO-8601 author date
    // Committer defaults to the author for a plain commit; cherry-pick/revert/rebase
    // preserve the original Author while recording who replayed it here. Older commit
    // objects predate these fields and deserialize with them null/empty.
    public string? Committer { get; set; }
    public string? CommitTime { get; set; } // ISO-8601 commit date
    /// <summary>SSH-format signature over <see cref="SignablePayload"/>, if signed.</summary>
    public string? Signature { get; set; }

    [JsonIgnore] public string CommitterOrAuthor => string.IsNullOrEmpty(Committer) ? Author : Committer;
    [JsonIgnore] public string CommitDate => string.IsNullOrEmpty(CommitTime) ? Time : CommitTime;

    /// <summary>The exact bytes that get signed — the object as-is but with the
    /// signature field cleared, so signing and verifying agree on the payload.</summary>
    public string SignablePayload()
    {
        string? saved = Signature;
        Signature = null;
        try { return ToJson(); } finally { Signature = saved; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, RepoJson.Options);
    public static CommitObject FromJson(string json) =>
        JsonSerializer.Deserialize<CommitObject>(json, RepoJson.Options)
        ?? throw new FormatException("invalid commit object");
}

/// <summary>
/// An annotated tag object (git's tag object): a named, dated, optionally signed
/// pointer to a commit. Stored as a content-addressed object whose hash the
/// <c>refs/tags/&lt;name&gt;</c> ref then holds — unlike a lightweight tag, whose ref
/// holds the commit hash directly.
/// </summary>
public sealed class TagObject
{
    public string Object { get; set; } = "";   // target hash (a commit)
    public string Type { get; set; } = "commit";
    public string Tag { get; set; } = "";       // the tag name
    public string Tagger { get; set; } = "";
    public string Time { get; set; } = "";      // ISO-8601
    public string Message { get; set; } = "";
    public string? Signature { get; set; }

    public string SignablePayload()
    {
        string? saved = Signature;
        Signature = null;
        try { return ToJson(); } finally { Signature = saved; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, RepoJson.Options);
    public static TagObject FromJson(string json) =>
        JsonSerializer.Deserialize<TagObject>(json, RepoJson.Options)
        ?? throw new FormatException("invalid tag object");

    /// <summary>Parses <paramref name="text"/> as a tag object, or null if it isn't
    /// one (any other object — commit, tree, blob — yields null).</summary>
    public static TagObject? TryFromJson(string text)
    {
        if (text.Length == 0 || text[0] != '{') return null;
        try
        {
            TagObject? t = JsonSerializer.Deserialize<TagObject>(text, RepoJson.Options);
            return t is not null && t.Object.Length > 0 && t.Tag.Length > 0 && t.Tagger.Length > 0 ? t : null;
        }
        catch (JsonException) { return null; }
    }
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
