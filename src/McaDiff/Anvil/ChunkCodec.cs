using fNbt;

namespace McaDiff.Anvil;

/// <summary>Thrown when a chunk uses a compression scheme we cannot decode (e.g. LZ4).</summary>
public sealed class UnsupportedChunkException(ChunkPos pos, ChunkCompression compression)
    : Exception($"Chunk {pos} uses unsupported compression {compression}.")
{
    public ChunkPos Pos { get; } = pos;
    public ChunkCompression Compression { get; } = compression;
}

/// <summary>Turns a <see cref="RawChunk"/>'s compressed payload into an NBT tree.</summary>
public static class ChunkCodec
{
    /// <summary>
    /// Decompresses and parses the chunk into its root <see cref="NbtCompound"/>.
    /// fNbt handles GZip/ZLib/None directly; LZ4 throws
    /// <see cref="UnsupportedChunkException"/>.
    /// </summary>
    public static NbtCompound Decode(RawChunk chunk)
    {
        NbtCompression compression = chunk.Compression switch
        {
            ChunkCompression.GZip => NbtCompression.GZip,
            ChunkCompression.ZLib => NbtCompression.ZLib,
            ChunkCompression.None => NbtCompression.None,
            _ => throw new UnsupportedChunkException(chunk.Pos, chunk.Compression),
        };

        var file = new NbtFile { BigEndian = true };
        file.LoadFromBuffer(chunk.Payload, 0, chunk.Payload.Length, compression);
        return file.RootTag;
    }

    /// <summary>
    /// Compresses an NBT root into a chunk payload (the body that follows the
    /// length/compression header inside a region file). Companion to <see cref="Decode"/>.
    /// </summary>
    public static byte[] Encode(NbtCompound root, ChunkCompression compression)
    {
        NbtCompression mapped = compression switch
        {
            ChunkCompression.GZip => NbtCompression.GZip,
            ChunkCompression.ZLib => NbtCompression.ZLib,
            ChunkCompression.None => NbtCompression.None,
            _ => throw new NotSupportedException($"Cannot encode chunk with compression {compression}."),
        };
        return new NbtFile(root) { BigEndian = true }.SaveToBuffer(mapped);
    }

    /// <summary>
    /// Loads a standalone NBT file (e.g. <c>level.dat</c>, <c>playerdata/*.dat</c>).
    /// Compression is auto-detected (these are usually GZip).
    /// </summary>
    public static NbtCompound LoadNbtFile(string path)
    {
        var file = new NbtFile { BigEndian = true };
        file.LoadFromFile(path); // single-arg overload auto-detects compression
        return file.RootTag;
    }

    /// <summary>Saves a standalone NBT file (defaults to GZip, as Minecraft writes).</summary>
    public static void SaveNbtFile(string path, NbtCompound root, NbtCompression compression = NbtCompression.GZip)
        => new NbtFile(root) { BigEndian = true }.SaveToFile(path, compression);
}
