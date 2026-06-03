using fNbt;

namespace McaDiff.Diff;

/// <summary>
/// Derives stable identity keys for the elements of an NBT list so that lists
/// behaving as sets (block entities, entities) can be matched by identity
/// instead of by position — which avoids reporting a reorder as a wholesale
/// rewrite. Returns <c>null</c> when no reliable identity exists, in which case
/// the comparer falls back to index alignment.
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
            string? key = KeyOf((NbtCompound)list[i]);
            if (key is null || !seen.Add(key))
                return null; // no identity, or a collision — bail to index alignment
            keys[i] = key;
        }
        return keys;
    }

    private static string? KeyOf(NbtCompound c)
    {
        // Block entities & some POI records: integer block coordinates.
        if (c.Get("x") is { TagType: NbtTagType.Int } tx &&
            c.Get("y") is { TagType: NbtTagType.Int } ty &&
            c.Get("z") is { TagType: NbtTagType.Int } tz)
            return $"@{tx.IntValue},{ty.IntValue},{tz.IntValue}";

        // Entities (1.16+): UUID stored as a 4-int array.
        if (c.Get("UUID") is { TagType: NbtTagType.IntArray } uuid &&
            uuid.IntArrayValue is { Length: 4 } v)
            return $"uuid:{v[0]:x8}{v[1]:x8}{v[2]:x8}{v[3]:x8}";

        // Older entities: split most/least longs.
        if (c.Get("UUIDMost") is { TagType: NbtTagType.Long } most &&
            c.Get("UUIDLeast") is { TagType: NbtTagType.Long } least)
            return $"uuid:{most.LongValue:x16}{least.LongValue:x16}";

        // Inventory/container items: the slot is the per-list identity.
        if (c.Get("Slot") is { TagType: NbtTagType.Byte } slot)
            return $"slot:{slot.ByteValue}";

        // Attributes, modifiers, and similar records identified by a string "id".
        // Falls back to index alignment automatically if ids collide (handled by
        // the uniqueness check in TryGetKeys).
        if (c.Get("id") is { TagType: NbtTagType.String } id && !string.IsNullOrEmpty(id.StringValue))
            return $"id:{id.StringValue}";

        return null;
    }
}
