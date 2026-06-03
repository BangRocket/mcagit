using System.Buffers.Binary;
using fNbt;
using McaDiff.Anvil;

namespace McaDiff.Tests;

/// <summary>Helpers to synthesize NBT and real-format Anvil region files for tests.</summary>
internal static class TestAnvil
{
    /// <summary>Builds a root compound (named "") from name/tag pairs.</summary>
    public static NbtCompound Root(params NbtTag[] tags)
    {
        var c = new NbtCompound("");
        foreach (NbtTag t in tags) c.Add(t);
        return c;
    }

    public static NbtCompound BlockEntity(string id, int x, int y, int z, params NbtTag[] extra)
    {
        var c = new NbtCompound
        {
            new NbtString("id", id),
            new NbtInt("x", x),
            new NbtInt("y", y),
            new NbtInt("z", z),
        };
        foreach (NbtTag t in extra) c.Add(t);
        return c;
    }

    public static NbtCompound DeepClone(NbtCompound c) => (NbtCompound)c.Clone();

    /// <summary>
    /// Writes a real Anvil region file containing a single chunk (zlib-compressed),
    /// using the standard 8 KiB header + sector layout.
    /// </summary>
    public static void WriteSingleChunkRegion(string path, ChunkPos pos, NbtCompound root)
    {
        byte[] payload = new NbtFile(root) { BigEndian = true }.SaveToBuffer(NbtCompression.ZLib);

        int total = 5 + payload.Length; // 4-byte length + 1-byte compression + payload
        int sectorCount = (total + RegionFile.SectorSize - 1) / RegionFile.SectorSize;

        var header = new byte[RegionFile.SectorSize * 2];
        int e = pos.RegionIndex * 4;
        const int offsetSectors = 2; // first data sector after the 8 KiB header
        header[e] = (byte)(offsetSectors >> 16);
        header[e + 1] = (byte)(offsetSectors >> 8);
        header[e + 2] = (byte)offsetSectors;
        header[e + 3] = (byte)sectorCount;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(RegionFile.SectorSize + e, 4), 1_700_000_000);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        fs.Write(header);

        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length + 1);
        fs.Write(lenBuf);
        fs.WriteByte((byte)ChunkCompression.ZLib);
        fs.Write(payload);

        int pad = sectorCount * RegionFile.SectorSize - total;
        if (pad > 0) fs.Write(new byte[pad]);
    }

    /// <summary>Creates a unique temp directory for a test and returns its path.</summary>
    public static string TempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"mcadiff-test-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
