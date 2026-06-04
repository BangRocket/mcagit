namespace McaDiff.Repo;

/// <summary>
/// A dumb object-storage bucket (S3/Azure Blob): keyed blobs with ETag-based optimistic
/// concurrency. Deliberately minimal — all repository protocol logic lives in
/// <see cref="BucketTransport"/>, so a backend is just these few operations.
/// </summary>
public interface IBucket
{
    /// <summary>The blob's bytes and ETag, or (null, null) if it doesn't exist.</summary>
    (byte[]? Data, string? ETag) Get(string key);

    /// <summary>Unconditional write (returns the new ETag).</summary>
    void Put(string key, byte[] data);

    /// <summary>Conditional write: succeeds only if the current ETag equals
    /// <paramref name="expectedETag"/> (null ⇒ "must not already exist"). Returns false if the
    /// precondition fails (a concurrent writer changed it) — the caller re-reads and retries.</summary>
    bool PutIfMatch(string key, byte[] data, string? expectedETag);

    /// <summary>Keys under <paramref name="prefix"/>.</summary>
    IReadOnlyList<string> List(string prefix);

    void Delete(string key);
}

/// <summary>An in-process <see cref="IBucket"/> for tests — models ETag-conditional writes
/// (the S3 <c>If-Match</c> / Azure lease semantics) so concurrency logic can be exercised
/// without a real cloud account.</summary>
public sealed class InMemoryBucket : IBucket
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Data, string ETag)> _store = new(StringComparer.Ordinal);
    private long _seq;

    public (byte[]?, string?) Get(string key)
    {
        lock (_gate) return _store.TryGetValue(key, out var v) ? ((byte[]?)v.Data, (string?)v.ETag) : (null, null);
    }

    public void Put(string key, byte[] data)
    {
        lock (_gate) _store[key] = (data, NextETag());
    }

    public bool PutIfMatch(string key, byte[] data, string? expectedETag)
    {
        lock (_gate)
        {
            string? current = _store.TryGetValue(key, out var v) ? v.ETag : null;
            if (current != expectedETag) return false;
            _store[key] = (data, NextETag());
            return true;
        }
    }

    public IReadOnlyList<string> List(string prefix)
    {
        lock (_gate) return _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public void Delete(string key)
    {
        lock (_gate) _store.Remove(key);
    }

    private string NextETag() => (++_seq).ToString();
}
