using System.Text.Json;
using System.Text.Json.Serialization;
using McaGit.Diff;

namespace McaGit.Output;

/// <summary>Renders a <see cref="WorldDiff"/> as structured JSON for scripting.</summary>
public static class JsonDiffFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(WorldDiff diff, TextWriter outw)
    {
        int chunksChanged = 0, nbtChanges = 0;
        foreach (FileDiff f in diff.Files)
        {
            nbtChanges += f.Changes.Count;
            foreach (ChunkDiff ch in f.Chunks)
            {
                chunksChanged++;
                nbtChanges += ch.Changes.Count;
            }
        }

        var dto = new
        {
            worldA = diff.PathA,
            worldB = diff.PathB,
            summary = new
            {
                filesChanged = diff.Files.Count,
                chunksChanged,
                nbtChanges,
            },
            files = diff.Files.Select(f => new
            {
                path = f.RelativePath,
                category = f.Category,
                kind = f.Kind,
                status = f.Status,
                itemCount = f.ItemCount,
                error = f.Error,
                chunks = f.Chunks.Count == 0 ? null : f.Chunks.Select(c => new
                {
                    x = c.Pos.X,
                    z = c.Pos.Z,
                    status = c.Status,
                    error = c.Error,
                    changes = c.Changes.Count == 0 ? null : c.Changes.Select(ToChangeDto),
                }),
                changes = f.Changes.Count == 0 ? null : f.Changes.Select(ToChangeDto),
            }),
        };

        outw.WriteLine(JsonSerializer.Serialize(dto, Options));
    }

    private static object ToChangeDto(NbtChange ch) => new
    {
        path = ch.Path,
        kind = ch.Kind,
        old = ch.OldValue,
        @new = ch.NewValue,
        oldType = ch.OldType,
        newType = ch.NewType,
        note = ch.Note,
    };
}
