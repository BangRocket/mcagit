using System.IO.Compression;
using System.Text;

namespace McaDiff.Repo;

/// <summary>A transport that can store many objects in one shot (a pack), instead of one
/// network round-trip per object — implemented by bucket backends.</summary>
public interface IBatchTransport
{
    void PutObjects(IReadOnlyList<(string Hash, byte[] Content)> objects);
}

/// <summary>
/// Serverless repository transport over a dumb object-storage <see cref="IBucket"/> (S3/Azure).
/// There's no daemon, so the protocol runs client-side: pushes bundle missing objects into one
/// content-addressed pack (reusing <see cref="Packfile"/>), uploaded with its index plus a
/// CAS-guarded <c>packs/manifest</c>; refs are tiny blobs updated with an ETag compare-and-swap
/// (the fast-forward check is enforced client-side, in <see cref="RemoteOps.PushTo"/>). Per push
/// it's ≈3 bucket writes regardless of how many chunks changed. Bucket layout:
/// <code>
/// &lt;prefix&gt;/HEAD              &lt;prefix&gt;/refs/heads/&lt;b&gt;   &lt;prefix&gt;/refs/tags/&lt;t&gt;
/// &lt;prefix&gt;/packs/&lt;id&gt;     &lt;prefix&gt;/packs/&lt;id&gt;.idx     &lt;prefix&gt;/packs/manifest
/// </code>
/// </summary>
public sealed class BucketTransport : IRemoteTransport, IBatchTransport
{
    private readonly IBucket _bucket;
    private readonly string _prefix;
    private readonly string _tempDir;
    private readonly Dictionary<string, Packfile> _openPacks = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _hashToPack; // object hash → pack id (lazy, from the .idx blobs)

    public BucketTransport(IBucket bucket, string prefix)
    {
        _bucket = bucket;
        _prefix = prefix.Trim('/');
        _tempDir = Directory.CreateTempSubdirectory("mcadiff-bucket").FullName;
    }

    private string Key(string suffix) => _prefix.Length == 0 ? suffix : $"{_prefix}/{suffix}";

    // ---- refs ----

    public RefAdvertisement ListRefs()
    {
        Dictionary<string, string> Read(string prefix)
        {
            var d = new Dictionary<string, string>();
            string full = Key(prefix);
            foreach (string key in _bucket.List(full))
                if (_bucket.Get(key).Data is { } data)
                    d[key[full.Length..]] = Encoding.UTF8.GetString(data).Trim();
            return d;
        }
        string? head = _bucket.Get(Key("HEAD")).Data is { } h ? Encoding.UTF8.GetString(h).Trim() : null;
        return new RefAdvertisement(Read("refs/heads/"), Read("refs/tags/"), head);
    }

    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
    {
        string key = Key($"refs/heads/{branch}");
        (byte[]? data, string? etag) = _bucket.Get(key);
        string? current = data is null ? null : Encoding.UTF8.GetString(data).Trim();
        if (!force && current != expectedOld)
            throw new InvalidOperationException($"ref {branch} moved on the remote (stale push) — fetch + retry");
        if (!_bucket.PutIfMatch(key, Encoding.UTF8.GetBytes(newHash + "\n"), etag))
            throw new InvalidOperationException($"ref {branch} changed concurrently — fetch + retry");
        if (_bucket.Get(Key("HEAD")).Data is null) // first push: make clone pick the right default branch
            _bucket.Put(Key("HEAD"), Encoding.UTF8.GetBytes(branch + "\n"));
    }

    // ---- objects ----

    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes)
    {
        EnsureIndex();
        return hashes.Where(h => !_hashToPack!.ContainsKey(h)).ToList();
    }

    public byte[] GetObject(string hash)
    {
        EnsureIndex();
        if (!_hashToPack!.TryGetValue(hash, out string? packId))
            throw new InvalidDataException($"object not present in remote bucket: {hash}");
        byte[] content = OpenPack(packId).Read(hash)
            ?? throw new InvalidDataException($"object {hash} missing from its pack");
        return ObjectStore.Compress(content); // GetObject returns the loose/compressed form
    }

    /// <summary>Single-object fallback (used only if RemoteOps doesn't take the batch path).</summary>
    public void PutObject(string hash, byte[] compressed) => PutObjects([(hash, Inflate(compressed))]);

    public void PutObjects(IReadOnlyList<(string Hash, byte[] Content)> objects)
    {
        if (objects.Count == 0) return;
        var byHash = objects.ToDictionary(o => o.Hash, o => o.Content, StringComparer.Ordinal);
        List<string> ordered = objects.Select(o => o.Hash)
            .OrderByDescending(h => byHash[h].Length).ThenBy(h => h, StringComparer.Ordinal).ToList();

        string staging = Path.Combine(_tempDir, "stage-" + Guid.NewGuid().ToString("N"));
        string? id = Packfile.Write(staging, ordered, h => byHash[h]);
        if (id is null) return;
        try
        {
            string packPath = Path.Combine(staging, "pack", $"pack-{id}.pack");
            _bucket.Put(Key($"packs/{id}"), File.ReadAllBytes(packPath));
            _bucket.Put(Key($"packs/{id}.idx"), File.ReadAllBytes(Path.ChangeExtension(packPath, ".idx")));
            AppendToManifest(id);
            _hashToPack = null; // a new pack landed — rebuild the index on next use
        }
        finally { try { Directory.Delete(staging, true); } catch { /* best effort */ } }
    }

    private void AppendToManifest(string id)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            (byte[]? data, string? etag) = _bucket.Get(Key("packs/manifest"));
            List<string> ids = data is null ? [] : Lines(data);
            if (ids.Contains(id)) return; // already recorded
            ids.Add(id);
            byte[] body = Encoding.UTF8.GetBytes(string.Join('\n', ids) + "\n");
            if (_bucket.PutIfMatch(Key("packs/manifest"), body, etag)) return; // CAS: only if unchanged since read
        }
        throw new InvalidOperationException("packs/manifest is contended — too many concurrent pushes");
    }

    // ---- pack index / download ----

    private void EnsureIndex()
    {
        if (_hashToPack is not null) return;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_bucket.Get(Key("packs/manifest")).Data is { } manifest)
            foreach (string packId in Lines(manifest))
            {
                RequireValidPackId(packId); // manifest is attacker-controlled (issue #23)
                if (_bucket.Get(Key($"packs/{packId}.idx")).Data is { } idx)
                    foreach (string h in Packfile.IndexHashes(idx))
                        map[h] = packId;
            }
        _hashToPack = map;
    }

    // A pack id from the bucket's manifest flows into local file paths; a value like "../../evil"
    // would let a hostile bucket write outside the temp dir. Pin it to the 40-hex PackId shape, the
    // same gate ObjectStore.IsValidHash gives loose objects.
    private static void RequireValidPackId(string packId)
    {
        if (packId.Length != 40 || !packId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new InvalidDataException($"remote bucket advertised a malformed pack id: '{packId}'");
    }

    private Packfile OpenPack(string packId)
    {
        RequireValidPackId(packId);
        if (_openPacks.TryGetValue(packId, out Packfile? cached)) return cached;
        byte[] pack = _bucket.Get(Key($"packs/{packId}")).Data ?? throw new InvalidDataException($"pack {packId} not found");
        byte[] idx = _bucket.Get(Key($"packs/{packId}.idx")).Data ?? throw new InvalidDataException($"index for {packId} not found");
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);
        string packPath = Path.Combine(packDir, $"pack-{packId}.pack");
        File.WriteAllBytes(packPath, pack);
        File.WriteAllBytes(Path.ChangeExtension(packPath, ".idx"), idx);
        Packfile pf = Packfile.Open(packPath);
        _openPacks[packId] = pf;
        return pf;
    }

    private static List<string> Lines(byte[] data) =>
        Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static byte[] Inflate(byte[] compressed) => SafeInflate.Zlib(compressed); // bounded (issue #21)

    public void Dispose()
    {
        foreach (Packfile p in _openPacks.Values) p.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }
}
