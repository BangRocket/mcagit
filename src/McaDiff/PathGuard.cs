namespace McaDiff;

/// <summary>
/// Path-traversal confinement for untrusted relative paths (manifest entry keys,
/// patch file paths, ref names received from a remote). Combines a root with a
/// relative path and verifies the canonicalized result stays inside the root —
/// defeating <c>../</c> escapes and absolute-path overrides (which <c>Path.Combine</c>
/// silently honors).
/// </summary>
public static class PathGuard
{
    /// <summary>Returns the full path of <paramref name="rel"/> under <paramref name="root"/>,
    /// or throws if it would escape <paramref name="root"/>.</summary>
    public static string Confine(string root, string rel)
    {
        string baseFull = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(baseFull, rel));
        string prefix = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        if (full != baseFull && !full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"unsafe path escapes the target directory: '{rel}'");
        return full;
    }
}
