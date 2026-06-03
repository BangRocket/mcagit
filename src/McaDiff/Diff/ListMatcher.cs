using fNbt;
using McaDiff.Nbt;

namespace McaDiff.Diff;

/// <summary>
/// Derives stable identity keys for the elements of an NBT list so that lists
/// behaving as sets (block entities, entities) can be matched by identity
/// instead of by position — which avoids reporting a reorder as a wholesale
/// rewrite. Returns <c>null</c> when no reliable identity exists, in which case
/// the comparer falls back to index alignment. Per-element identity lives in
/// <see cref="NbtIdentity"/> (shared with the patch path resolver).
/// </summary>
public static class ListMatcher
{
    /// <summary>
    /// Returns one key per element if every element has a unique identity;
    /// otherwise <c>null</c>.
    /// </summary>
    public static string[]? TryGetKeys(NbtList list)
    {
        if (list.Count == 0 || list.ListType != NbtTagType.Compound)
            return null;

        var keys = new string[list.Count];
        var seen = new HashSet<string>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            string? key = NbtIdentity.KeyOf((NbtCompound)list[i]);
            if (key is null || !seen.Add(key))
                return null; // no identity, or a collision — bail to index alignment
            keys[i] = key;
        }
        return keys;
    }
}
