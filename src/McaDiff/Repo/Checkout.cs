using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;

namespace McaDiff.Repo;

/// <summary>Materializes a <see cref="Manifest"/> back into a playable world directory.</summary>
public static class Checkout
{
    public static void Materialize(Repository repo, Manifest manifest, string worldOut)
    {
        Directory.CreateDirectory(worldOut);

        foreach ((string rel, SortedDictionary<string, string> chunks) in manifest.Regions)
        {
            var raws = new List<RawChunk>(chunks.Count);
            foreach ((string posKey, string hash) in chunks)
            {
                ChunkPos pos = ParsePos(posKey);
                NbtCompound root = NbtCanonical.Deserialize(repo.Objects.Read(hash));
                raws.Add(new RawChunk(pos, ChunkCompression.ZLib,
                    ChunkCodec.Encode(root, ChunkCompression.ZLib), external: false, timestamp: 0));
            }
            RegionWriter.Write(Path.Combine(worldOut, rel), raws); // empty list → valid empty region
        }

        foreach ((string rel, string hash) in manifest.Nbt)
        {
            string outFile = Path.Combine(worldOut, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            ChunkCodec.SaveNbtFile(outFile, NbtCanonical.Deserialize(repo.Objects.Read(hash)));
        }

        foreach ((string rel, string hash) in manifest.Blobs)
        {
            string outFile = Path.Combine(worldOut, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, repo.Objects.Read(hash));
        }
    }

    private static ChunkPos ParsePos(string key)
    {
        int comma = key.IndexOf(',');
        return new ChunkPos(int.Parse(key[..comma]), int.Parse(key[(comma + 1)..]));
    }
}
