using System.Globalization;
using fNbt;

namespace McaDiff.Nbt;

/// <summary>
/// Parses and resolves the dotted/bracketed paths that the diff emits
/// (<c>a.b</c>, <c>list[3]</c>, identity <c>block_entities[@x,y,z]</c> /
/// <c>[uuid:…]</c> / <c>[slot:n]</c> / <c>[id:…]</c>) against a live NBT tree, so a
/// patch op can read the current value and set/add/remove the node.
/// </summary>
/// <remarks>
/// Limitation: a compound key containing a literal <c>.</c> or <c>[</c> is not
/// representable. Real Minecraft keys don't, so this is acceptable for v1.
/// </remarks>
public static class NbtPath
{
    private readonly record struct Seg(bool IsBracket, string Text);

    /// <summary>The compound key of the terminal segment, or null for a list element / root.</summary>
    public static string? TerminalName(string path)
    {
        List<Seg> segs = Parse(path);
        if (segs.Count == 0) return null;
        Seg last = segs[^1];
        return last.IsBracket ? null : last.Text;
    }

    /// <summary>Reads the tag at <paramref name="path"/>, or null if any step is absent.</summary>
    public static NbtTag? Get(NbtTag root, string path)
    {
        List<Seg> segs = Parse(path);
        NbtTag? cur = root;
        foreach (Seg seg in segs)
        {
            cur = Step(cur, seg);
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>
    /// Sets the node at <paramref name="path"/> to <paramref name="newTag"/>
    /// (null = remove). Returns false if the parent container is missing or of the
    /// wrong shape (treat as a conflict). <paramref name="newTag"/> must already be
    /// named to match the terminal compound key, or nameless for a list element.
    /// </summary>
    public static bool Set(NbtTag root, string path, NbtTag? newTag)
    {
        List<Seg> segs = Parse(path);
        if (segs.Count == 0) return false; // root replace is handled by the caller

        NbtTag? parent = root;
        for (int k = 0; k < segs.Count - 1; k++)
        {
            parent = Step(parent, segs[k]);
            if (parent is null) return false;
        }

        Seg terminal = segs[^1];
        if (terminal.IsBracket)
        {
            if (parent is not NbtList list) return false;
            if (TryIndex(terminal.Text, out int i))
            {
                if (newTag is null) { if (i >= 0 && i < list.Count) list.RemoveAt(i); }
                else if (i >= 0 && i < list.Count) { list.RemoveAt(i); list.Insert(i, newTag); }
                else if (i >= list.Count) list.Add(newTag);
                else return false;
            }
            else
            {
                int idx = FindIdentity(list, terminal.Text);
                if (newTag is null) { if (idx >= 0) list.RemoveAt(idx); }
                else if (idx >= 0) { list.RemoveAt(idx); list.Insert(idx, newTag); }
                else list.Add(newTag);
            }
            return true;
        }

        if (parent is not NbtCompound comp) return false;
        comp.Remove(terminal.Text);
        if (newTag is not null) comp.Add(newTag);
        return true;
    }

    private static NbtTag? Step(NbtTag? tag, Seg seg)
    {
        if (seg.IsBracket)
        {
            if (tag is not NbtList list) return null;
            if (TryIndex(seg.Text, out int i))
                return i >= 0 && i < list.Count ? list[i] : null;
            int idx = FindIdentity(list, seg.Text);
            return idx >= 0 ? list[idx] : null;
        }
        if (tag is not NbtCompound comp) return null;
        return comp.Get(seg.Text);
    }

    private static int FindIdentity(NbtList list, string key)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] is NbtCompound c && NbtIdentity.KeyOf(c) == key)
                return i;
        return -1;
    }

    private static bool TryIndex(string text, out int index)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);

    private static List<Seg> Parse(string path)
    {
        var segs = new List<Seg>();
        int i = 0, n = path.Length;
        while (i < n)
        {
            char c = path[i];
            if (c == '.') { i++; continue; }
            if (c == '[')
            {
                int j = path.IndexOf(']', i + 1);
                if (j < 0) throw new FormatException($"Unterminated '[' in path: {path}");
                string inner = path.Substring(i + 1, j - (i + 1));
                if (inner.Length == 0) throw new FormatException($"Empty '[]' in path: {path}");
                segs.Add(new Seg(true, inner));
                i = j + 1;
            }
            else
            {
                int j = i;
                while (j < n && path[j] != '.' && path[j] != '[') j++;
                segs.Add(new Seg(false, path[i..j]));
                i = j;
            }
        }
        return segs;
    }
}
