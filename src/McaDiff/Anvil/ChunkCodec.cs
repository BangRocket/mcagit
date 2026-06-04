using System.IO.Compression;
using fNbt;
using K4os.Compression.LZ4.Streams;
using McaDiff.Nbt;

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
    // Generous per-object inflate cap: real chunks and loose files are far below this, but a crafted
    // payload could otherwise inflate to gigabytes and OOM the process (issue #21).
    private const long MaxNbtBytes = 128L * 1024 * 1024;

    public static NbtCompound Decode(RawChunk chunk)
    {
        // We decompress every scheme ourselves (bounded), then parse the plain NBT — so the inflate is
        // size-capped and the depth scan runs before fNbt's recursive parser (issues #21, #22). LZ4
        // (type 4, since 1.20.5) is an LZ4 frame wrapping uncompressed NBT; type 127 Custom is opaque.
        byte[] raw = chunk.Compression switch
        {
            ChunkCompression.None => chunk.Payload,
            ChunkCompression.ZLib => InflateBounded(new ZLibStream(new MemoryStream(chunk.Payload), CompressionMode.Decompress)),
            ChunkCompression.GZip => InflateBounded(new GZipStream(new MemoryStream(chunk.Payload), CompressionMode.Decompress)),
            ChunkCompression.Lz4 => InflateBounded(LZ4Stream.Decode(new MemoryStream(chunk.Payload))),
            _ => throw new UnsupportedChunkException(chunk.Pos, chunk.Compression), // type 127 Custom
        };
        return ParseChecked(raw);
    }

    /// <summary>Drains a decompressor to a byte[], throwing past <see cref="MaxNbtBytes"/>.</summary>
    private static byte[] InflateBounded(Stream decompressor)
    {
        using (decompressor)
        using (var outMs = new MemoryStream())
        {
            byte[] buf = new byte[81920];
            long total = 0;
            int r;
            while ((r = decompressor.Read(buf, 0, buf.Length)) > 0)
            {
                total += r;
                if (total > MaxNbtBytes) throw new InvalidDataException($"NBT inflates past {MaxNbtBytes} bytes (decompression bomb?)");
                outMs.Write(buf, 0, r);
            }
            return outMs.ToArray();
        }
    }

    /// <summary>Depth-scans raw NBT (pre-parse, can't be skipped) then parses with fNbt.</summary>
    private static NbtCompound ParseChecked(byte[] raw)
    {
        NbtDepthGuard.Check(raw); // reject pathological nesting before fNbt recurses into a stack overflow
        var file = new NbtFile { BigEndian = true };
        file.LoadFromBuffer(raw, 0, raw.Length, NbtCompression.None);
        return file.RootTag;
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
        byte[] bytes = File.ReadAllBytes(path);
        // Auto-detect compression by magic (as fNbt does), but decompress ourselves (bounded) and
        // depth-check before parsing — same untrusted-input guards as chunk decode (issues #21, #22).
        byte[] raw = bytes is [0x1f, 0x8b, ..] ? InflateBounded(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            : bytes is [0x78, _, ..] ? InflateBounded(new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress))
            : bytes;
        return ParseChecked(raw);
    }

    /// <summary>Saves a standalone NBT file (defaults to GZip, as Minecraft writes).</summary>
    public static void SaveNbtFile(string path, NbtCompound root, NbtCompression compression = NbtCompression.GZip)
        => new NbtFile(root) { BigEndian = true }.SaveToFile(path, compression);
}
