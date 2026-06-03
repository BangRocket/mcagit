using System.Collections.Concurrent;
using System.Text.Json;

namespace McaDiff.Repo;

/// <summary>
/// Persistent map from a chunk's <em>compressed payload</em> hash to its stored
/// chunk-object hash. Lets re-commits of a mostly-unchanged world skip decoding +
/// canonicalizing chunks whose raw bytes are unchanged (the common backup case).
/// Always content-derived, so entries never go stale; commit still verifies the
/// object actually exists before trusting a hit.
/// </summary>
public sealed class ChunkCache
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, string> _map;

    private ChunkCache(string path, ConcurrentDictionary<string, string> map)
    {
        _path = path;
        _map = map;
    }

    public static ChunkCache Load(string repoDir)
    {
        string path = Path.Combine(repoDir, "chunkcache.json");
        ConcurrentDictionary<string, string> map = new();
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded is not null) map = new ConcurrentDictionary<string, string>(loaded);
            }
            catch { /* corrupt cache → start empty (it only ever accelerates) */ }
        }
        return new ChunkCache(path, map);
    }

    public bool TryGet(string key, out string hash) => _map.TryGetValue(key, out hash!);

    public void Set(string key, string hash) => _map[key] = hash;

    public void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_map));
}
