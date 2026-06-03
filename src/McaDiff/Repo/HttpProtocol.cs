using System.Text.Json;
using System.Text.Json.Serialization;

namespace McaDiff.Repo;

/// <summary>Wire DTO for a ref update (push). <c>new</c> is a keyword, hence the attribute.</summary>
public sealed class RefUpdate
{
    [JsonPropertyName("old")] public string? Old { get; set; }
    [JsonPropertyName("new")] public string New { get; set; } = "";
    [JsonPropertyName("force")] public bool Force { get; set; }
}

internal static class HttpProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };
}
