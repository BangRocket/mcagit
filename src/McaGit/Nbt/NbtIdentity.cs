using fNbt;

namespace McaGit.Nbt;

/// <summary>
/// Derives a stable identity key for a list-element compound, so set-like NBT
/// lists (block entities, entities, inventories, attributes) can be matched by
/// identity rather than by position. Shared by the diff (<c>Diff.ListMatcher</c>)
/// and the patch path resolver (<c>Nbt.NbtPath</c>). Returns <c>null</c> when no
/// reliable identity exists.
/// </summary>
/// <remarks>
/// VERSION-1 CONTRACT: the key formats and priority order below are part of the
/// <c>.mcapatch</c> v1 path syntax. Changing any rule here (a format, the priority,
/// or which fields are consulted) is a BREAKING change — patches written under the
/// old rule would resolve to a different (or absent) list element. Any such change
/// must bump <see cref="Patch.WorldPatch.Version"/> and gate it in <c>PatchApplier</c>.
/// Known limitation: an identity value containing a literal <c>]</c> (e.g. a modded
/// id) is mis-parsed by <c>NbtPath</c>, which truncates at the first <c>]</c>.
/// </remarks>
public static class NbtIdentity
{
    public static string? KeyOf(NbtCompound c)
    {
        // Block entities & some POI records: integer block coordinates.
        if (c.Get("x") is { TagType: NbtTagType.Int } tx &&
            c.Get("y") is { TagType: NbtTagType.Int } ty &&
            c.Get("z") is { TagType: NbtTagType.Int } tz)
            return $"@{tx.IntValue},{ty.IntValue},{tz.IntValue}";

        // POI records: position is a length-3 int array named "pos" (unique within a section),
        // so reordered records aren't reported as spurious modifications.
        if (c.Get("pos") is { TagType: NbtTagType.IntArray } poiPos && poiPos.IntArrayValue is { Length: 3 } pp)
            return $"@{pp[0]},{pp[1]},{pp[2]}";

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
        if (c.Get("id") is { TagType: NbtTagType.String } id && !string.IsNullOrEmpty(id.StringValue))
            return $"id:{id.StringValue}";

        return null;
    }
}
