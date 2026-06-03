using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace McaDiff.Repo;

/// <summary>
/// A content-addressed, deduplicated object store: every object is keyed by the
/// SHA-256 of its content and stored once, zlib-compressed, at
/// <c>objects/&lt;aa&gt;/&lt;rest&gt;</c>. Identical content (e.g. an unchanged
/// chunk in two snapshots) collapses to a single object.
/// </summary>
public sealed class ObjectStore
{
    private readonly string _objectsDir;

    public ObjectStore(string repoDir) => _objectsDir = Path.Combine(repoDir, "objects");

    /// <summary>Stores <paramref name="content"/> if new; returns its hash either way.</summary>
    public string Write(ReadOnlySpan<byte> content)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        string path = PathFor(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file then move, so a concurrent writer never sees a partial object.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = File.Create(tmp))
            using (var z = new ZLibStream(fs, CompressionLevel.Optimal))
                z.Write(content);
            if (File.Exists(path)) File.Delete(tmp); else File.Move(tmp, path);
        }
        return hash;
    }

    public string WriteText(string text) => Write(Encoding.UTF8.GetBytes(text));

    public byte[] Read(string hash)
    {
        using var fs = File.OpenRead(PathFor(hash));
        using var z = new ZLibStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    public string ReadText(string hash) => Encoding.UTF8.GetString(Read(hash));

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    /// <summary>Total number of stored objects (used to demonstrate dedup in tests).</summary>
    public int Count() => Directory.Exists(_objectsDir)
        ? Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".tmp"))
        : 0;

    private string PathFor(string hash) => Path.Combine(_objectsDir, hash[..2], hash[2..]);
}
