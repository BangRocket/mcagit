using fNbt;
using McaDiff.Nbt;

namespace McaDiff.Diff;

/// <summary>
/// Walks two NBT trees in parallel and reports leaf-level changes to an
/// <see cref="IDiffSink"/>. Compounds are matched by key, lists matched by
/// identity where possible (see <see cref="ListMatcher"/>) else by index. The
/// walk is representation-agnostic — see <see cref="NbtChangeSink"/> for the
/// display rendering used by <c>Compare</c>.
/// </summary>
public static class NbtComparer
{
    /// <summary>Convenience wrapper producing the flat, sorted display change list.</summary>
    public static List<NbtChange> Compare(NbtTag a, NbtTag b, NbtDiffOptions options)
    {
        var sink = new NbtChangeSink(options);
        Walk(a, b, sink);
        sink.Changes.Sort(static (x, y) => string.CompareOrdinal(x.Path, y.Path));
        return sink.Changes;
    }

    /// <summary>Drives the tree walk against an arbitrary sink (path prefix is the root "").</summary>
    public static void Walk(NbtTag a, NbtTag b, IDiffSink sink) => CompareTag("", a, b, sink, 0);

    private static void CompareTag(string path, NbtTag a, NbtTag b, IDiffSink sink, int depth)
    {
        if (depth > NbtCanonical.MaxDepth)
            throw new InvalidDataException($"NBT nesting exceeds {NbtCanonical.MaxDepth} levels");
        if (a.TagType != b.TagType)
        {
            sink.TypeChanged(path, a, b);
            return;
        }

        switch (a.TagType)
        {
            case NbtTagType.Compound:
                CompareCompound(path, (NbtCompound)a, (NbtCompound)b, sink, depth);
                break;
            case NbtTagType.List:
                CompareList(path, (NbtList)a, (NbtList)b, sink, depth);
                break;
            case NbtTagType.ByteArray:
            case NbtTagType.IntArray:
            case NbtTagType.LongArray:
                if (!NbtEquality.DeepEquals(a, b))
                    sink.ArrayChanged(path, a, b);
                break;
            default:
                if (!ValueRepr.ScalarEquals(a, b))
                    sink.Modified(path, a, b);
                break;
        }
    }

    private static void CompareCompound(string path, NbtCompound a, NbtCompound b, IDiffSink sink, int depth)
    {
        var bByName = new Dictionary<string, NbtTag>(b.Count);
        foreach (NbtTag tb in b)
            bByName[tb.Name!] = tb;

        foreach (NbtTag ta in a)
        {
            string child = Child(path, ta.Name!);
            if (bByName.Remove(ta.Name!, out NbtTag? tb))
                CompareTag(child, ta, tb, sink, depth + 1);
            else
                sink.Removed(child, ta);
        }

        // Emit added keys in name order so an extracted patch is byte-reproducible (the patch
        // sink doesn't re-sort, and fNbt iteration order isn't a contract).
        foreach (NbtTag tb in bByName.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            sink.Added(Child(path, tb.Name!), tb);
    }

    private static void CompareList(string path, NbtList a, NbtList b, IDiffSink sink, int depth)
    {
        string[]? keysA = ListMatcher.TryGetKeys(a);
        string[]? keysB = keysA is null ? null : ListMatcher.TryGetKeys(b);

        if (keysA is not null && keysB is not null)
        {
            CompareKeyedList(path, a, keysA, b, keysB, sink, depth);
            return;
        }

        int common = Math.Min(a.Count, b.Count);
        for (int i = 0; i < common; i++)
            CompareTag($"{path}[{i}]", a[i], b[i], sink, depth + 1);
        for (int i = common; i < a.Count; i++)
            sink.Removed($"{path}[{i}]", a[i]);
        for (int i = common; i < b.Count; i++)
            sink.Added($"{path}[{i}]", b[i]);
    }

    private static void CompareKeyedList(string path, NbtList a, string[] keysA, NbtList b, string[] keysB, IDiffSink sink, int depth)
    {
        var bIndex = new Dictionary<string, int>(keysB.Length);
        for (int i = 0; i < keysB.Length; i++)
            bIndex[keysB[i]] = i;

        for (int i = 0; i < keysA.Length; i++)
        {
            string label = $"{path}[{keysA[i]}]";
            if (bIndex.Remove(keysA[i], out int j))
                CompareTag(label, a[i], b[j], sink, depth + 1);
            else
                sink.Removed(label, a[i]);
        }

        foreach ((string key, int j) in bIndex)
            sink.Added($"{path}[{key}]", b[j]);
    }

    internal static string Child(string path, string name)
        => path.Length == 0 ? name : $"{path}.{name}";
}
