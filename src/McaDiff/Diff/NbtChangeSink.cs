using fNbt;

namespace McaDiff.Diff;

/// <summary>
/// <see cref="IDiffSink"/> that renders the human-facing display diff: added /
/// removed subtrees are flattened to one row per leaf, and arrays are summarized
/// (<c>long[37] — 3 of 37 entries differ</c>) unless
/// <see cref="NbtDiffOptions.ExpandArrays"/> is set.
/// </summary>
public sealed class NbtChangeSink(NbtDiffOptions options) : IDiffSink
{
    public List<NbtChange> Changes { get; } = [];

    public void Modified(string path, NbtTag a, NbtTag b)
        => Changes.Add(new NbtChange(path, ChangeKind.Modified,
            ValueRepr.Scalar(a), ValueRepr.Scalar(b), a.TagType, b.TagType));

    public void TypeChanged(string path, NbtTag a, NbtTag b)
        => Changes.Add(new NbtChange(path, ChangeKind.TypeChanged,
            ValueRepr.Describe(a), ValueRepr.Describe(b), a.TagType, b.TagType));

    public void Added(string path, NbtTag value) => Flatten(path, value, ChangeKind.Added);

    public void Removed(string path, NbtTag value) => Flatten(path, value, ChangeKind.Removed);

    public void ArrayChanged(string path, NbtTag a, NbtTag b)
    {
        ReadOnlySpan<long> la = AsLongs(a, out int lenA);
        ReadOnlySpan<long> lb = AsLongs(b, out int lenB);
        int common = Math.Min(lenA, lenB);
        int diffCount = 0;
        for (int i = 0; i < common; i++)
            if (la[i] != lb[i]) diffCount++;

        if (options.ExpandArrays)
        {
            for (int i = 0; i < common; i++)
                if (la[i] != lb[i])
                    Changes.Add(new NbtChange($"{path}[{i}]", ChangeKind.Modified,
                        la[i].ToString(), lb[i].ToString(), a.TagType, b.TagType));
            for (int i = common; i < lenA; i++)
                Changes.Add(new NbtChange($"{path}[{i}]", ChangeKind.Removed, la[i].ToString(), null, a.TagType));
            for (int i = common; i < lenB; i++)
                Changes.Add(new NbtChange($"{path}[{i}]", ChangeKind.Added, null, lb[i].ToString(), null, b.TagType));
            return;
        }

        string note = lenA == lenB
            ? $"{diffCount} of {lenA} entries differ"
            : $"length {lenA} → {lenB}" + (diffCount > 0 ? $", {diffCount} of {common} differ" : "");
        Changes.Add(new NbtChange(path, ChangeKind.Modified,
            ValueRepr.ArraySummary(a), ValueRepr.ArraySummary(b), a.TagType, b.TagType, note));
    }

    private void Flatten(string path, NbtTag tag, ChangeKind kind)
    {
        switch (tag.TagType)
        {
            case NbtTagType.Compound:
                var c = (NbtCompound)tag;
                if (c.Count == 0) { AddLeaf(path, tag, kind); break; }
                foreach (NbtTag child in c)
                    Flatten(NbtComparer.Child(path, child.Name!), child, kind);
                break;
            case NbtTagType.List:
                var list = (NbtList)tag;
                if (list.Count == 0) { AddLeaf(path, tag, kind); break; }
                for (int i = 0; i < list.Count; i++)
                    Flatten($"{path}[{i}]", list[i], kind);
                break;
            default:
                AddLeaf(path, tag, kind);
                break;
        }
    }

    private void AddLeaf(string path, NbtTag tag, ChangeKind kind)
    {
        string repr = ValueRepr.Describe(tag);
        Changes.Add(kind == ChangeKind.Removed
            ? new NbtChange(path, ChangeKind.Removed, repr, null, tag.TagType)
            : new NbtChange(path, ChangeKind.Added, null, repr, null, tag.TagType));
    }

    /// <summary>Widens any NBT array to <see cref="long"/> for uniform comparison.</summary>
    private static ReadOnlySpan<long> AsLongs(NbtTag tag, out int length)
    {
        switch (tag.TagType)
        {
            case NbtTagType.ByteArray:
                byte[] bsrc = tag.ByteArrayValue;
                length = bsrc.Length;
                var bdst = new long[bsrc.Length];
                for (int i = 0; i < bsrc.Length; i++) bdst[i] = (sbyte)bsrc[i];
                return bdst;
            case NbtTagType.IntArray:
                int[] isrc = tag.IntArrayValue;
                length = isrc.Length;
                var idst = new long[isrc.Length];
                for (int i = 0; i < isrc.Length; i++) idst[i] = isrc[i];
                return idst;
            case NbtTagType.LongArray:
                length = tag.LongArrayValue.Length;
                return tag.LongArrayValue;
            default:
                length = 0;
                return ReadOnlySpan<long>.Empty;
        }
    }
}
