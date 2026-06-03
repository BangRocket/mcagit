using fNbt;

namespace McaDiff.Diff;

/// <summary>
/// Recursively diffs two NBT trees into a flat, path-addressed list of
/// <see cref="NbtChange"/>. Compounds are matched by key, arrays compared
/// element-wise (summarized unless <see cref="NbtDiffOptions.ExpandArrays"/>),
/// and lists matched by identity where possible (see <see cref="ListMatcher"/>),
/// else by index.
/// </summary>
public static class NbtComparer
{
    public static List<NbtChange> Compare(NbtTag a, NbtTag b, NbtDiffOptions options)
    {
        var changes = new List<NbtChange>();
        CompareTag("", a, b, options, changes);
        changes.Sort(static (x, y) => string.CompareOrdinal(x.Path, y.Path));
        return changes;
    }

    private static void CompareTag(string path, NbtTag a, NbtTag b, NbtDiffOptions opt, List<NbtChange> sink)
    {
        if (a.TagType != b.TagType)
        {
            sink.Add(new NbtChange(path, ChangeKind.TypeChanged,
                ValueRepr.Describe(a), ValueRepr.Describe(b), a.TagType, b.TagType));
            return;
        }

        switch (a.TagType)
        {
            case NbtTagType.Compound:
                CompareCompound(path, (NbtCompound)a, (NbtCompound)b, opt, sink);
                break;
            case NbtTagType.List:
                CompareList(path, (NbtList)a, (NbtList)b, opt, sink);
                break;
            case NbtTagType.ByteArray:
            case NbtTagType.IntArray:
            case NbtTagType.LongArray:
                CompareArray(path, a, b, opt, sink);
                break;
            default:
                if (!ValueRepr.ScalarEquals(a, b))
                    sink.Add(new NbtChange(path, ChangeKind.Modified,
                        ValueRepr.Scalar(a), ValueRepr.Scalar(b), a.TagType, b.TagType));
                break;
        }
    }

    private static void CompareCompound(string path, NbtCompound a, NbtCompound b, NbtDiffOptions opt, List<NbtChange> sink)
    {
        var bByName = new Dictionary<string, NbtTag>(b.Count);
        foreach (NbtTag tb in b)
            bByName[tb.Name!] = tb;

        foreach (NbtTag ta in a)
        {
            string child = Child(path, ta.Name!);
            if (bByName.Remove(ta.Name!, out NbtTag? tb))
                CompareTag(child, ta, tb, opt, sink);
            else
                Flatten(child, ta, ChangeKind.Removed, sink);
        }

        foreach (NbtTag tb in bByName.Values)
            Flatten(Child(path, tb.Name!), tb, ChangeKind.Added, sink);
    }

    private static void CompareList(string path, NbtList a, NbtList b, NbtDiffOptions opt, List<NbtChange> sink)
    {
        string[]? keysA = ListMatcher.TryGetKeys(a);
        string[]? keysB = keysA is null ? null : ListMatcher.TryGetKeys(b);

        if (keysA is not null && keysB is not null)
        {
            CompareKeyedList(path, a, keysA, b, keysB, opt, sink);
            return;
        }

        int common = Math.Min(a.Count, b.Count);
        for (int i = 0; i < common; i++)
            CompareTag($"{path}[{i}]", a[i], b[i], opt, sink);
        for (int i = common; i < a.Count; i++)
            Flatten($"{path}[{i}]", a[i], ChangeKind.Removed, sink);
        for (int i = common; i < b.Count; i++)
            Flatten($"{path}[{i}]", b[i], ChangeKind.Added, sink);
    }

    private static void CompareKeyedList(string path, NbtList a, string[] keysA, NbtList b, string[] keysB, NbtDiffOptions opt, List<NbtChange> sink)
    {
        var bIndex = new Dictionary<string, int>(keysB.Length);
        for (int i = 0; i < keysB.Length; i++)
            bIndex[keysB[i]] = i;

        for (int i = 0; i < keysA.Length; i++)
        {
            string label = $"{path}[{keysA[i]}]";
            if (bIndex.Remove(keysA[i], out int j))
                CompareTag(label, a[i], b[j], opt, sink);
            else
                Flatten(label, a[i], ChangeKind.Removed, sink);
        }

        foreach ((string key, int j) in bIndex)
            Flatten($"{path}[{key}]", b[j], ChangeKind.Added, sink);
    }

    private static void CompareArray(string path, NbtTag a, NbtTag b, NbtDiffOptions opt, List<NbtChange> sink)
    {
        ReadOnlySpan<long> la = AsLongs(a, out int lenA);
        ReadOnlySpan<long> lb = AsLongs(b, out int lenB);

        int common = Math.Min(lenA, lenB);
        int diffCount = 0;
        for (int i = 0; i < common; i++)
            if (la[i] != lb[i]) diffCount++;

        if (diffCount == 0 && lenA == lenB)
            return; // identical

        if (opt.ExpandArrays)
        {
            for (int i = 0; i < common; i++)
                if (la[i] != lb[i])
                    sink.Add(new NbtChange($"{path}[{i}]", ChangeKind.Modified,
                        la[i].ToString(), lb[i].ToString(), a.TagType, b.TagType));
            for (int i = common; i < lenA; i++)
                sink.Add(new NbtChange($"{path}[{i}]", ChangeKind.Removed, la[i].ToString(), null, a.TagType));
            for (int i = common; i < lenB; i++)
                sink.Add(new NbtChange($"{path}[{i}]", ChangeKind.Added, null, lb[i].ToString(), null, b.TagType));
            return;
        }

        string note = lenA == lenB
            ? $"{diffCount} of {lenA} entries differ"
            : $"length {lenA} → {lenB}" + (diffCount > 0 ? $", {diffCount} of {common} differ" : "");
        sink.Add(new NbtChange(path, ChangeKind.Modified,
            ValueRepr.ArraySummary(a), ValueRepr.ArraySummary(b), a.TagType, b.TagType, note));
    }

    /// <summary>Widens any NBT array to <see cref="long"/> for uniform comparison.</summary>
    private static ReadOnlySpan<long> AsLongs(NbtTag tag, out int length)
    {
        switch (tag.TagType)
        {
            case NbtTagType.ByteArray:
            {
                byte[] src = tag.ByteArrayValue;
                length = src.Length;
                var dst = new long[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = (sbyte)src[i];
                return dst;
            }
            case NbtTagType.IntArray:
            {
                int[] src = tag.IntArrayValue;
                length = src.Length;
                var dst = new long[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            case NbtTagType.LongArray:
                length = tag.LongArrayValue.Length;
                return tag.LongArrayValue;
            default:
                length = 0;
                return ReadOnlySpan<long>.Empty;
        }
    }

    /// <summary>Emits one change per leaf of an added/removed subtree (arrays summarized).</summary>
    private static void Flatten(string path, NbtTag tag, ChangeKind kind, List<NbtChange> sink)
    {
        switch (tag.TagType)
        {
            case NbtTagType.Compound:
                foreach (NbtTag child in (NbtCompound)tag)
                    Flatten(Child(path, child.Name!), child, kind, sink);
                // An empty compound still represents a change worth noting.
                if (((NbtCompound)tag).Count == 0)
                    AddLeaf(path, tag, kind, sink);
                break;
            case NbtTagType.List:
            {
                var list = (NbtList)tag;
                if (list.Count == 0) { AddLeaf(path, tag, kind, sink); break; }
                for (int i = 0; i < list.Count; i++)
                    Flatten($"{path}[{i}]", list[i], kind, sink);
                break;
            }
            default:
                AddLeaf(path, tag, kind, sink);
                break;
        }
    }

    private static void AddLeaf(string path, NbtTag tag, ChangeKind kind, List<NbtChange> sink)
    {
        string repr = ValueRepr.Describe(tag);
        sink.Add(kind == ChangeKind.Removed
            ? new NbtChange(path, ChangeKind.Removed, repr, null, tag.TagType)
            : new NbtChange(path, ChangeKind.Added, null, repr, null, tag.TagType));
    }

    private static string Child(string path, string name)
        => path.Length == 0 ? name : $"{path}.{name}";
}
