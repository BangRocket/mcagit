using System.Buffers.Binary;

namespace McaDiff.Anvil;

/// <summary>On-disk compression scheme of a chunk payload.</summary>
public enum ChunkCompression
{
    GZip = 1,
    ZLib = 2,
    None = 3,
    Lz4 = 4,
    Custom = 127,
}

/// <summary>
/// Reader for the Anvil region container (<c>r.X.Z.mca</c>). Parses the 8 KiB
/// header (location + timestamp tables) and exposes each present chunk's raw
/// compressed bytes. Pure container concern — NBT parsing lives in
/// <see cref="ChunkCodec"/>.
/// </summary>
public sealed class RegionFile
{
    public const int SectorSize = 4096;

    public int RegionX { get; }
    public int RegionZ { get; }
    public string Path { get; }

    private readonly Dictionary<ChunkPos, RawChunk> _chunks;

    private RegionFile(string path, int regionX, int regionZ, Dictionary<ChunkPos, RawChunk> chunks)
    {
        Path = path;
        RegionX = regionX;
        RegionZ = regionZ;
        _chunks = chunks;
    }

    public IReadOnlyCollection<RawChunk> Chunks => _chunks.Values;
    public int Count => _chunks.Count;

    public bool TryGet(ChunkPos pos, out RawChunk chunk) => _chunks.TryGetValue(pos, out chunk!);

    /// <summary>
    /// Parses the region <paramref name="path"/>. The region coordinates are
    /// taken from the file name (<c>r.X.Z.mca</c>) so chunk coordinates are absolute.
    /// </summary>
    public static RegionFile Open(string path) => Parse(path, File.ReadAllBytes(path));

    /// <summary>
    /// Parses already-loaded region <paramref name="bytes"/>. Lets callers that
    /// have read the file (e.g. for a whole-file equality check) avoid a re-read.
    /// </summary>
    public static RegionFile Parse(string path, byte[] bytes)
    {
        (int rx, int rz) = ParseRegionCoords(path);
        var chunks = new Dictionary<ChunkPos, RawChunk>(1024);

        if (bytes.Length < SectorSize * 2)
            return new RegionFile(path, rx, rz, chunks); // truncated/empty region — no chunks

        for (int i = 0; i < 1024; i++)
        {
            int e = i * 4;
            int offsetSectors = (bytes[e] << 16) | (bytes[e + 1] << 8) | bytes[e + 2];
            int sectorCount = bytes[e + 3];
            if (offsetSectors == 0 || sectorCount == 0)
                continue; // chunk not generated

            long start = (long)offsetSectors * SectorSize;
            if (start + 5 > bytes.Length)
                continue; // location points past EOF — skip defensively

            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan((int)start, 4));
            if (length <= 0)
                continue;

            byte compByte = bytes[(int)start + 4];
            bool external = (compByte & 0x80) != 0;
            var compression = (ChunkCompression)(compByte & 0x7F);

            ChunkPos pos = ChunkPos.FromRegionIndex(rx, rz, i);
            int timestamp = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(SectorSize + e, 4));

            byte[] payload;
            if (external)
            {
                // Oversized chunk: body lives in c.X.Z.mcc next to the region file.
                string mcc = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(path) ?? ".", $"c.{pos.X}.{pos.Z}.mcc");
                if (!File.Exists(mcc))
                    continue; // external body missing — nothing to read
                payload = File.ReadAllBytes(mcc);
            }
            else
            {
                // payload length excludes the 1-byte compression tag; clamp to EOF.
                int dataLen = length - 1;
                long dataStart = start + 5;
                if (dataStart + dataLen > bytes.Length)
                    dataLen = (int)(bytes.Length - dataStart);
                if (dataLen <= 0)
                    continue;
                payload = bytes.AsSpan((int)dataStart, dataLen).ToArray();
            }

            chunks[pos] = new RawChunk(pos, compression, payload, external, timestamp);
        }

        return new RegionFile(path, rx, rz, chunks);
    }

    /// <summary>Counts present chunks by reading only the 4 KiB location table (no payloads).</summary>
    public static int CountChunks(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[SectorSize];
        if (fs.Read(header) < SectorSize)
            return 0;
        int count = 0;
        for (int i = 0; i < 1024; i++)
        {
            int e = i * 4;
            int offset = (header[e] << 16) | (header[e + 1] << 8) | header[e + 2];
            if (offset != 0 && header[e + 3] != 0)
                count++;
        }
        return count;
    }

    /// <summary>Extracts the (X, Z) region coordinates from an <c>r.X.Z.mca</c> file name.</summary>
    public static (int X, int Z) ParseRegionCoords(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path); // r.X.Z
        string[] parts = name.Split('.');
        if (parts.Length == 3 && parts[0] == "r"
            && int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int z))
            return (x, z);
        return (0, 0); // non-standard name — coords unknown, chunk coords become region-local
    }
}
