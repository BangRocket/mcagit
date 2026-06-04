using fNbt;

namespace McaDiff.Nbt;

/// <summary>
/// Deterministic, uncompressed serialization of an NBT root — the canonical form
/// hashed and stored as a repository object. Hashing decoded NBT (rather than the
/// region's compressed payload) means an untouched chunk dedups across snapshots
/// even when the container recompresses it differently. Compound keys are sorted
/// recursively (lists, which are ordered, are left intact) so two semantically
/// equal chunks that differ only in NBT key order still hash identically.
/// </summary>
public static class NbtCanonical
{
    /// <summary>Max NBT nesting we'll process — matches Minecraft's own 512-deep cap. A
    /// hostile chunk nested thousands deep would otherwise overflow the stack (uncatchable).</summary>
    internal const int MaxDepth = 512;

    public static byte[] Serialize(NbtCompound root)
        => new NbtFile((NbtCompound)Sorted(root, 0)) { BigEndian = true }.SaveToBuffer(NbtCompression.None);

    public static NbtCompound Deserialize(byte[] bytes)
    {
        var file = new NbtFile { BigEndian = true };
        file.LoadFromBuffer(bytes, 0, bytes.Length, NbtCompression.None);
        return file.RootTag;
    }

    /// <summary>Deep clone with compound children ordered by name (lists keep order).</summary>
    private static NbtTag Sorted(NbtTag tag, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"NBT nesting exceeds {MaxDepth} levels");
        switch (tag.TagType)
        {
            case NbtTagType.Compound:
                var c = tag.Name is null ? new NbtCompound() : new NbtCompound(tag.Name);
                foreach (NbtTag child in ((NbtCompound)tag).OrderBy(t => t.Name, StringComparer.Ordinal))
                    c.Add(Sorted(child, depth + 1));
                return c;
            case NbtTagType.List:
                var src = (NbtList)tag;
                var list = MakeList(tag.Name, src.ListType);
                foreach (NbtTag e in src) list.Add(Sorted(e, depth + 1));
                return list;
            default:
                return (NbtTag)tag.Clone();
        }
    }

    private static NbtList MakeList(string? name, NbtTagType type) => (name, type) switch
    {
        (null, NbtTagType.Unknown) => new NbtList(),
        (null, _) => new NbtList(type),
        (_, NbtTagType.Unknown) => new NbtList(name),
        _ => new NbtList(name, type),
    };
}
