using fNbt;

namespace McaDiff.Nbt;

/// <summary>
/// Deterministic, uncompressed serialization of an NBT root — the canonical form
/// hashed and stored as a repository object. Hashing decoded NBT (rather than the
/// region's compressed payload) means an untouched chunk dedups across snapshots
/// even when the container recompresses it differently.
/// </summary>
public static class NbtCanonical
{
    public static byte[] Serialize(NbtCompound root)
        => new NbtFile(root) { BigEndian = true }.SaveToBuffer(NbtCompression.None);

    public static NbtCompound Deserialize(byte[] bytes)
    {
        var file = new NbtFile { BigEndian = true };
        file.LoadFromBuffer(bytes, 0, bytes.Length, NbtCompression.None);
        return file.RootTag;
    }
}
