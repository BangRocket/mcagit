using fNbt;
using K4os.Compression.LZ4.Streams;

namespace McaDiff.Anvil;

/// <summary>Thrown when a chunk uses a compression scheme we cannot decode (type 127 Custom).</summary>
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
        // LZ4 (type 4, region-file-compression=lz4 since 1.20.5) is an LZ4 *frame* wrapping
        // uncompressed NBT — decode the frame, then hand the plain NBT to fNbt.
        if (chunk.Compression == ChunkCompression.Lz4)
        {
            byte[] nbt = Lz4Decode(chunk.Payload);
            var lz4File = new NbtFile { BigEndian = true };
            lz4File.LoadFromBuffer(nbt, 0, nbt.Length, NbtCompression.None);
            return lz4File.RootTag;
        }

        NbtCompression compression = chunk.Compression switch
        {
            ChunkCompression.GZip => NbtCompression.GZip,
            ChunkCompression.ZLib => NbtCompression.ZLib,
            ChunkCompression.None => NbtCompression.None,
            _ => throw new UnsupportedChunkException(chunk.Pos, chunk.Compression), // type 127 Custom
        };

        var file = new NbtFile { BigEndian = true };
        file.LoadFromBuffer(chunk.Payload, 0, chunk.Payload.Length, compression);
        return file.RootTag;
    }

    private static byte[] Lz4Decode(byte[] frame)
    {
        using var input = new MemoryStream(frame);
        using var lz4 = LZ4Stream.Decode(input);
        using var output = new MemoryStream();
        lz4.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Lz4Encode(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var lz4 = LZ4Stream.Encode(output, leaveOpen: true)) lz4.Write(plain, 0, plain.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Compresses an NBT root into a chunk payload (the body that follows the
    /// length/compression header inside a region file). Companion to <see cref="Decode"/>.
    /// </summary>
    public static byte[] Encode(NbtCompound root, ChunkCompression compression)
    {
        if (compression == ChunkCompression.Lz4)
            return Lz4Encode(new NbtFile(root) { BigEndian = true }.SaveToBuffer(NbtCompression.None));

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
