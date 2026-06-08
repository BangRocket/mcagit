using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace McaGit.Nbt;

/// <summary>
/// Allocation-light canonicalization: produces the canonical NBT bytes <b>directly from raw
/// (uncompressed, big-endian) NBT</b>, byte-for-byte equal to <see cref="Serialize"/>(parse(raw)) but
/// without materializing an fNbt tree. The only transformation canonicalization makes is sorting each
/// compound's members by name (Ordinal); every scalar/array/string payload, every name, and every list
/// (which stays ordered) is copied verbatim from the input. On the commit hot path this is the
/// difference between a cold snapshot being allocation-bound (an fNbt object per tag, twice over) or
/// not. A <see cref="NbtCanonicalRawTests"/> differential test pins the output to the tree path.
/// Throws <see cref="InvalidDataException"/> on malformed/truncated input (callers fall back to a blob).
/// </summary>
public static partial class NbtCanonical
{
    // NBT tag type ids.
    private const byte TagEnd = 0, TagByte = 1, TagShort = 2, TagInt = 3, TagLong = 4, TagFloat = 5,
        TagDouble = 6, TagByteArray = 7, TagString = 8, TagList = 9, TagCompound = 10, TagIntArray = 11, TagLongArray = 12;

    public static byte[] CanonicalizeRaw(ReadOnlySpan<byte> raw)
    {
        var w = new ArrayBufferWriter<byte>(Math.Max(raw.Length, 16));
        int pos = 0;
        byte rootType = ReadByte(raw, ref pos);
        // A root that isn't a (named) compound is not valid Minecraft NBT, but mirror the tree path:
        // it serializes whatever the root tag is. In practice the root is always a TAG_Compound.
        WriteByte(w, rootType);
        if (rootType != TagEnd)
        {
            CopyName(raw, ref pos, w);
            WritePayload(raw, ref pos, rootType, 0, w);
        }
        if (pos != raw.Length) throw new InvalidDataException("trailing bytes after NBT root");
        return w.WrittenSpan.ToArray();
    }

    /// <summary>A compound member: its decoded name (for sorting) and the input byte ranges of its
    /// name field and payload (emitted verbatim / recursively, never re-encoded).</summary>
    private readonly record struct Member(string Name, byte Type, int NameFieldStart, int PayloadStart, int PayloadEnd);

    private static void WritePayload(ReadOnlySpan<byte> raw, ref int pos, byte type, int depth, ArrayBufferWriter<byte> w)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"NBT nesting exceeds {MaxDepth} levels");
        switch (type)
        {
            case TagByte: Copy(raw, ref pos, 1, w); break;
            case TagShort: Copy(raw, ref pos, 2, w); break;
            case TagInt or TagFloat: Copy(raw, ref pos, 4, w); break;
            case TagLong or TagDouble: Copy(raw, ref pos, 8, w); break;
            case TagString: { int n = ReadU16(raw, pos); Copy(raw, ref pos, 2L + n, w); break; }
            case TagByteArray: { int n = ReadLen(raw, pos, 1); Copy(raw, ref pos, 4L + n, w); break; }
            case TagIntArray: { int n = ReadLen(raw, pos, 4); Copy(raw, ref pos, 4L + 4L * n, w); break; }
            case TagLongArray: { int n = ReadLen(raw, pos, 8); Copy(raw, ref pos, 4L + 8L * n, w); break; }
            case TagList:
                {
                    if (pos + 5 > raw.Length) throw new InvalidDataException("truncated NBT list header");
                    byte elemType = raw[pos];
                    int count = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(pos + 1, 4));
                    if (count < 0) throw new InvalidDataException("negative NBT list length");
                    Copy(raw, ref pos, 5, w); // element type + count, verbatim (lists keep their order)
                    for (int i = 0; i < count; i++) WritePayload(raw, ref pos, elemType, depth + 1, w);
                    break;
                }
            case TagCompound: WriteCompound(raw, ref pos, depth, w); break;
            default: throw new InvalidDataException($"unknown NBT tag type {type}");
        }
    }

    private static void WriteCompound(ReadOnlySpan<byte> raw, ref int pos, int depth, ArrayBufferWriter<byte> w)
    {
        var members = new List<Member>();
        while (true)
        {
            byte type = ReadByte(raw, ref pos);
            if (type == TagEnd) break;
            int nameFieldStart = pos;                 // the 2-byte name length
            int nameLen = ReadU16(raw, pos);
            int nameStart = pos + 2;
            if ((long)nameStart + nameLen > raw.Length) throw new InvalidDataException("truncated NBT name");
            string name = Encoding.UTF8.GetString(raw.Slice(nameStart, nameLen));
            pos = nameStart + nameLen;
            int payloadStart = pos;
            SkipPayload(raw, ref pos, type, depth + 1);
            members.Add(new Member(name, type, nameFieldStart, payloadStart, pos));
        }
        // Compound keys are unique, so the sort needn't be stable; CompareOrdinal == StringComparer.Ordinal.
        members.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (Member m in members)
        {
            WriteByte(w, m.Type);
            w.Write(raw.Slice(m.NameFieldStart, m.PayloadStart - m.NameFieldStart)); // name (len + bytes) verbatim
            int p = m.PayloadStart;
            WritePayload(raw, ref p, m.Type, depth + 1, w);
        }
        WriteByte(w, TagEnd);
    }

    /// <summary>Advances <paramref name="pos"/> past a payload without emitting — used to find a
    /// compound member's byte span before the members are sorted.</summary>
    private static void SkipPayload(ReadOnlySpan<byte> raw, ref int pos, byte type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"NBT nesting exceeds {MaxDepth} levels");
        switch (type)
        {
            case TagByte: pos += 1; break;
            case TagShort: pos += 2; break;
            case TagInt or TagFloat: pos += 4; break;
            case TagLong or TagDouble: pos += 8; break;
            case TagString: { int n = ReadU16(raw, pos); Advance(raw, ref pos, 2L + n); break; }
            case TagByteArray: { int n = ReadLen(raw, pos, 1); Advance(raw, ref pos, 4L + n); break; }
            case TagIntArray: { int n = ReadLen(raw, pos, 4); Advance(raw, ref pos, 4L + 4L * n); break; }
            case TagLongArray: { int n = ReadLen(raw, pos, 8); Advance(raw, ref pos, 4L + 8L * n); break; }
            case TagList:
                {
                    if (pos + 5 > raw.Length) throw new InvalidDataException("truncated NBT list header");
                    byte elemType = raw[pos];
                    int count = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(pos + 1, 4));
                    if (count < 0) throw new InvalidDataException("negative NBT list length");
                    pos += 5;
                    for (int i = 0; i < count; i++) SkipPayload(raw, ref pos, elemType, depth + 1);
                    break;
                }
            case TagCompound:
                while (true)
                {
                    byte t = ReadByte(raw, ref pos);
                    if (t == TagEnd) break;
                    int nameLen = ReadU16(raw, pos);
                    Advance(raw, ref pos, 2L + nameLen);
                    SkipPayload(raw, ref pos, t, depth + 1);
                }
                break;
            default: throw new InvalidDataException($"unknown NBT tag type {type}");
        }
    }

    // ---- primitive reads (all bounds-checked: raw NBT is untrusted) ----

    private static byte ReadByte(ReadOnlySpan<byte> raw, ref int pos)
    {
        if (pos >= raw.Length) throw new InvalidDataException("truncated NBT");
        return raw[pos++];
    }

    private static int ReadU16(ReadOnlySpan<byte> raw, int pos)
    {
        if (pos + 2 > raw.Length) throw new InvalidDataException("truncated NBT");
        return BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos, 2));
    }

    /// <summary>Reads a 4-byte array element count and verifies <c>count * elemSize</c> bytes remain.</summary>
    private static int ReadLen(ReadOnlySpan<byte> raw, int pos, int elemSize)
    {
        if (pos + 4 > raw.Length) throw new InvalidDataException("truncated NBT");
        int n = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(pos, 4));
        if (n < 0 || (long)n * elemSize > raw.Length - pos - 4) throw new InvalidDataException("NBT array length out of range");
        return n;
    }

    private static void CopyName(ReadOnlySpan<byte> raw, ref int pos, ArrayBufferWriter<byte> w)
    {
        int n = ReadU16(raw, pos);
        Copy(raw, ref pos, 2L + n, w);
    }

    private static void Copy(ReadOnlySpan<byte> raw, ref int pos, long len, ArrayBufferWriter<byte> w)
    {
        if (len < 0 || pos + len > raw.Length) throw new InvalidDataException("truncated NBT payload");
        w.Write(raw.Slice(pos, (int)len));
        pos += (int)len;
    }

    private static void Advance(ReadOnlySpan<byte> raw, ref int pos, long len)
    {
        if (len < 0 || pos + len > raw.Length) throw new InvalidDataException("truncated NBT payload");
        pos += (int)len;
    }

    private static void WriteByte(ArrayBufferWriter<byte> w, byte b)
    {
        Span<byte> s = w.GetSpan(1);
        s[0] = b;
        w.Advance(1);
    }
}
