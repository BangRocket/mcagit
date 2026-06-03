using System.Buffers.Binary;

namespace McaDiff.Anvil;

/// <summary>
/// Writes a valid Anvil region file (<c>r.X.Z.mca</c>) from a set of chunks: the
/// 8 KiB header (location + timestamp tables) followed by sector-aligned chunk
/// bodies. Oversized chunks (&gt; 255 sectors) are spilled to an external
/// <c>c.X.Z.mcc</c> file with the high bit set, matching the format
/// <see cref="RegionFile"/> reads back.
/// </summary>
public static class RegionWriter
{
    private const int SectorSize = RegionFile.SectorSize;
    private const int MaxInlineSectors = 255; // sector count is a single byte

    public static void Write(string path, IEnumerable<RawChunk> chunks)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(dir);

        var ordered = chunks.OrderBy(c => c.Pos.RegionIndex).ToList();
        var header = new byte[SectorSize * 2];
        var bodies = new List<byte[]>(ordered.Count);

        int offsetSectors = 2; // chunk data starts right after the 8 KiB header
        foreach (RawChunk ch in ordered)
        {
            byte[] payload = ch.Payload;
            byte compByte = (byte)ch.Compression;

            int sectors = (5 + payload.Length + SectorSize - 1) / SectorSize;
            if (sectors > MaxInlineSectors)
            {
                // Spill to external .mcc; inline body becomes just the header byte.
                File.WriteAllBytes(Path.Combine(dir, $"c.{ch.Pos.X}.{ch.Pos.Z}.mcc"), payload);
                compByte = (byte)((byte)ch.Compression | 0x80);
                payload = [];
                sectors = 1;
            }

            var body = new byte[sectors * SectorSize];
            BinaryPrimitives.WriteInt32BigEndian(body, payload.Length + 1); // length includes the compression byte
            body[4] = compByte;
            payload.CopyTo(body, 5);
            bodies.Add(body);

            int e = ch.Pos.RegionIndex * 4;
            header[e] = (byte)(offsetSectors >> 16);
            header[e + 1] = (byte)(offsetSectors >> 8);
            header[e + 2] = (byte)offsetSectors;
            header[e + 3] = (byte)sectors;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(SectorSize + e, 4), ch.Timestamp);

            offsetSectors += sectors;
        }

        using var fs = File.Create(path);
        fs.Write(header);
        foreach (byte[] body in bodies) fs.Write(body);
    }
}
