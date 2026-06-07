using System.IO.Compression;

namespace McaGit.Repo;

/// <summary>
/// Output-bounded decompression / stream-drain helpers. Every place that inflates or drains an
/// <b>untrusted</b> stream (a chunk payload, a bucket/pack object, a remote HTTP body) must cap the
/// output, or a tiny crafted input can inflate to gigabytes and OOM the process before any hash or
/// size check runs (issue #21). The cap throws <see cref="InvalidDataException"/> — a catchable error,
/// not an OOM.
/// </summary>
public static class SafeInflate
{
    public const long DefaultMax = 512L * 1024 * 1024; // matches ObjectStore's object cap

    /// <summary>Inflates zlib bytes, throwing if the output would exceed <paramref name="max"/>.</summary>
    public static byte[] Zlib(byte[] compressed, long max = DefaultMax)
    {
        using var ms = new MemoryStream(compressed);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        return ReadBounded(z, max);
    }

    /// <summary>Reads <paramref name="source"/> to a byte[] capped at <paramref name="max"/> bytes.</summary>
    public static byte[] ReadBounded(Stream source, long max)
    {
        using var outMs = new MemoryStream();
        byte[] buf = new byte[81920];
        long total = 0;
        int r;
        while ((r = source.Read(buf, 0, buf.Length)) > 0)
        {
            total += r;
            if (total > max) throw new InvalidDataException($"data exceeds {max} bytes (decompression bomb / oversized response?)");
            outMs.Write(buf, 0, r);
        }
        return outMs.ToArray();
    }
}
