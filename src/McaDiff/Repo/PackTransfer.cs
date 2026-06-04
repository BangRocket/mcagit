using System.Buffers.Binary;

namespace McaDiff.Repo;

/// <summary>
/// Builds and ingests in-memory packs for batched wire transfer. A push of N new objects over
/// path/http/ssh becomes one pack instead of N round-trips, reusing the on-disk <see cref="Packfile"/>
/// format (delta-compressed). A received pack is <b>untrusted input</b>: every object is hash-verified
/// on ingest (<see cref="ObjectStore.ImportRaw"/> rejects a mismatch), so a tampered pack can't poison
/// the store.
/// </summary>
public static class PackTransfer
{
    /// <summary>Packs (hash, decompressed-content) pairs into <c>(pack, idx)</c> bytes, or null if empty.</summary>
    public static (byte[] Pack, byte[] Idx)? Build(IReadOnlyList<(string Hash, byte[] Content)> objects)
    {
        if (objects.Count == 0) return null;
        var byHash = objects.ToDictionary(o => o.Hash, o => o.Content, StringComparer.Ordinal);
        List<string> ordered = objects.Select(o => o.Hash)
            .OrderByDescending(h => byHash[h].Length).ThenBy(h => h, StringComparer.Ordinal).ToList();

        string staging = Path.Combine(Path.GetTempPath(), "mcadiff-packsend-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? id = Packfile.Write(staging, ordered, h => byHash[h]);
            if (id is null) return null;
            string packPath = Path.Combine(staging, "pack", $"pack-{id}.pack");
            return (File.ReadAllBytes(packPath), File.ReadAllBytes(Path.ChangeExtension(packPath, ".idx")));
        }
        finally { try { Directory.Delete(staging, true); } catch { /* best effort */ } }
    }

    /// <summary>Ingests a received pack into <paramref name="repo"/>, hash-verifying every object.
    /// Returns the number imported.</summary>
    public static int ImportInto(Repository repo, byte[] pack, byte[] idx)
    {
        string staging = Path.Combine(Path.GetTempPath(), "mcadiff-packrecv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string packPath = Path.Combine(staging, "incoming.pack");
        try
        {
            File.WriteAllBytes(packPath, pack);
            File.WriteAllBytes(Path.ChangeExtension(packPath, ".idx"), idx);
            using Packfile pf = Packfile.Open(packPath);
            int n = 0;
            foreach (string hash in Packfile.IndexHashes(idx))
            {
                byte[] content = pf.Read(hash) ?? throw new InvalidDataException($"pack is missing object {hash}");
                repo.Objects.ImportRaw(hash, ObjectStore.Compress(content)); // verifies hash(content) == hash
                n++;
            }
            return n;
        }
        finally { try { Directory.Delete(staging, true); } catch { /* best effort */ } }
    }

    // HTTP carries both blobs in one body: [4-byte BE idx length][idx][pack].

    public static byte[] FrameBody(byte[] pack, byte[] idx)
    {
        var body = new byte[4 + idx.Length + pack.Length];
        BinaryPrimitives.WriteInt32BigEndian(body, idx.Length);
        idx.CopyTo(body, 4);
        pack.CopyTo(body, 4 + idx.Length);
        return body;
    }

    public static (byte[] Pack, byte[] Idx) UnframeBody(byte[] body)
    {
        if (body.Length < 4) throw new InvalidDataException("truncated pack body");
        int idxLen = BinaryPrimitives.ReadInt32BigEndian(body);
        if (idxLen < 0 || 4L + idxLen > body.Length) throw new InvalidDataException("bad pack-body framing");
        byte[] idx = body[4..(4 + idxLen)];
        byte[] pack = body[(4 + idxLen)..];
        return (pack, idx);
    }
}
