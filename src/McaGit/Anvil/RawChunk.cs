namespace McaGit.Anvil;

/// <summary>
/// One chunk as it sits inside a region file: its position, the on-disk
/// compression scheme, the last-modified timestamp and the <em>raw, still
/// compressed</em> payload bytes.
/// </summary>
/// <remarks>
/// Keeping the compressed bytes lets the differ short-circuit unchanged chunks
/// without ever decompressing or parsing NBT (the dominant cost).
/// </remarks>
public sealed class RawChunk
{
    public ChunkPos Pos { get; }

    /// <summary>Raw compression-type byte from the chunk header (1=gzip, 2=zlib, 3=none, 4=lz4).</summary>
    public ChunkCompression Compression { get; }

    /// <summary>Compressed payload (NBT body), excluding the length/compression header.</summary>
    public byte[] Payload { get; }

    /// <summary>True when the payload was loaded from an external <c>.mcc</c> file.</summary>
    public bool External { get; }

    /// <summary>Unix epoch seconds from the region timestamp table (0 if absent).</summary>
    public int Timestamp { get; }

    public RawChunk(ChunkPos pos, ChunkCompression compression, byte[] payload, bool external, int timestamp)
    {
        Pos = pos;
        Compression = compression;
        Payload = payload;
        External = external;
        Timestamp = timestamp;
    }

    /// <summary>Byte-for-byte payload equality — the fast path for "unchanged chunk".</summary>
    public bool PayloadEquals(RawChunk other)
        => Compression == other.Compression && Payload.AsSpan().SequenceEqual(other.Payload);
}
