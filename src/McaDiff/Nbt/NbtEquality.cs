using fNbt;

namespace McaDiff.Nbt;

/// <summary>
/// Recursive value equality for NBT trees. Ignores each tag's own name (names
/// only matter as compound keys, which are compared structurally). Used by the
/// 3-way apply guard to decide whether the target's current value matches what
/// the patch expects.
/// </summary>
public static class NbtEquality
{
    public static bool DeepEquals(NbtTag? a, NbtTag? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.TagType != b.TagType) return false;

        switch (a.TagType)
        {
            case NbtTagType.Byte: return a.ByteValue == b.ByteValue;
            case NbtTagType.Short: return a.ShortValue == b.ShortValue;
            case NbtTagType.Int: return a.IntValue == b.IntValue;
            case NbtTagType.Long: return a.LongValue == b.LongValue;
            case NbtTagType.Float: return a.FloatValue.Equals(b.FloatValue);
            case NbtTagType.Double: return a.DoubleValue.Equals(b.DoubleValue);
            case NbtTagType.String: return string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal);
            case NbtTagType.ByteArray: return a.ByteArrayValue.AsSpan().SequenceEqual(b.ByteArrayValue);
            case NbtTagType.IntArray: return a.IntArrayValue.AsSpan().SequenceEqual(b.IntArrayValue);
            case NbtTagType.LongArray: return a.LongArrayValue.AsSpan().SequenceEqual(b.LongArrayValue);
            case NbtTagType.Compound: return CompoundEquals((NbtCompound)a, (NbtCompound)b);
            case NbtTagType.List: return ListEquals((NbtList)a, (NbtList)b);
            default: return false;
        }
    }

    private static bool CompoundEquals(NbtCompound a, NbtCompound b)
    {
        if (a.Count != b.Count) return false;
        foreach (NbtTag ta in a)
        {
            if (b.Get(ta.Name!) is not { } tb || !DeepEquals(ta, tb))
                return false;
        }
        return true;
    }

    private static bool ListEquals(NbtList a, NbtList b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!DeepEquals(a[i], b[i]))
                return false;
        return true;
    }
}
