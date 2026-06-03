using System.Text;
using McaDiff.Diff;

namespace McaDiff.Output;

/// <summary>Renders a <see cref="WorldDiff"/> as a colored, git-style unified diff.</summary>
public sealed class TextDiffFormatter
{
    private readonly Ansi _c;
    private readonly bool _summaryOnly;

    public TextDiffFormatter(Ansi ansi, bool summaryOnly)
    {
        _c = ansi;
        _summaryOnly = summaryOnly;
    }

    public void Write(WorldDiff diff, TextWriter outw)
    {
        if (!diff.HasDifferences)
        {
            outw.WriteLine(_c.Dim("No differences."));
            return;
        }

        int chunksChanged = 0, nbtChanges = 0;
        foreach (FileDiff file in diff.Files)
        {
            WriteFileHeader(file, outw);

            if (file.Error is not null)
            {
                outw.WriteLine("  " + _c.Red($"! error: {file.Error}"));
                continue;
            }

            if (file.Kind == UnitKind.Loose)
            {
                nbtChanges += file.Changes.Count;
                if (!_summaryOnly)
                    foreach (NbtChange ch in file.Changes)
                        outw.WriteLine("  " + FormatChange(ch));
            }
            else
            {
                foreach (ChunkDiff chunk in file.Chunks)
                {
                    chunksChanged++;
                    if (_summaryOnly) continue;
                    WriteChunkHeader(chunk, outw);
                    if (chunk.Error is not null)
                    {
                        outw.WriteLine("    " + _c.Red($"! error: {chunk.Error}"));
                        continue;
                    }
                    nbtChanges += chunk.Changes.Count;
                    foreach (NbtChange ch in chunk.Changes)
                        outw.WriteLine("    " + FormatChange(ch));
                }
                if (_summaryOnly)
                    foreach (ChunkDiff chunk in file.Chunks)
                        nbtChanges += chunk.Changes.Count;
            }

            outw.WriteLine();
        }

        WriteSummary(diff, chunksChanged, nbtChanges, outw);
    }

    private void WriteFileHeader(FileDiff file, TextWriter outw)
    {
        string tag = file.Kind == UnitKind.Region ? "--mca" : "--nbt";
        outw.WriteLine(_c.Bold(_c.Cyan($"diff {tag} {file.RelativePath}")));
        string? count = file.ItemCount is { } n ? $" ({n} chunks)" : "";
        switch (file.Status)
        {
            case DiffStatus.Added:
                outw.WriteLine(_c.Green("new file" + count));
                break;
            case DiffStatus.Removed:
                outw.WriteLine(_c.Red("deleted file" + count));
                break;
        }
    }

    private void WriteChunkHeader(ChunkDiff chunk, TextWriter outw)
    {
        string suffix = chunk.Status switch
        {
            DiffStatus.Added => " added",
            DiffStatus.Removed => " deleted",
            _ => "",
        };
        outw.WriteLine("  " + _c.Yellow($"@@ chunk {chunk.Pos}{suffix} @@"));
    }

    private string FormatChange(NbtChange ch)
    {
        switch (ch.Kind)
        {
            case ChangeKind.Added:
                return _c.Green($"+ {ch.Path}: {ch.NewValue}");
            case ChangeKind.Removed:
                return _c.Red($"- {ch.Path}: {ch.OldValue}");
            case ChangeKind.TypeChanged:
                return _c.Magenta($"± {ch.Path}: {ch.OldValue} ({ch.OldType}) → {ch.NewValue} ({ch.NewType})");
            default:
                var sb = new StringBuilder();
                sb.Append($"~ {ch.Path}: {ch.OldValue} → {ch.NewValue}");
                string line = _c.Yellow(sb.ToString());
                if (ch.Note is not null)
                    line += " " + _c.Dim($"({ch.Note})");
                return line;
        }
    }

    private void WriteSummary(WorldDiff diff, int chunksChanged, int nbtChanges, TextWriter outw)
    {
        int added = diff.Files.Count(f => f.Status == DiffStatus.Added);
        int removed = diff.Files.Count(f => f.Status == DiffStatus.Removed);
        int modified = diff.Files.Count(f => f.Status == DiffStatus.Modified);
        string s = $"{diff.Files.Count} files changed " +
                   $"({modified} modified, {added} added, {removed} deleted), " +
                   $"{chunksChanged} chunks, {nbtChanges} nbt changes";
        outw.WriteLine(_c.Bold(s));
    }
}
