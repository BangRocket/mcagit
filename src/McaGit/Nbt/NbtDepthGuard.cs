using System.Buffers.Binary;

namespace McaGit.Nbt;

/// <summary>
/// A non-recursive, allocation-light depth scan of raw (uncompressed) NBT bytes, run <b>before</b>
/// handing the buffer to fNbt. fNbt 1.0.0 parses recursively with no depth limit, so a chunk nesting
/// compounds/lists thousands deep overflows the native stack — an uncatchable
/// <c>StackOverflowException</c> that kills the whole process (issue #22). This walks the same
/// structure with an explicit stack (so the scan itself can't overflow) and throws a catchable
/// <see cref="InvalidDataException"/> past <see cref="MaxDepth"/>, before fNbt ever recurses. The
/// post-parse guards (<c>NbtCanonical</c>, <c>NbtComparer</c>) stay as defense-in-depth.
/// </summary>
public static class NbtDepthGuard
{
    public const int MaxDepth = 512; // matches NbtCanonical.MaxDepth — real Minecraft NBT is < 20 deep

    public static void Check(ReadOnlySpan<byte> nbt)
    {
        int p = 0;
        byte rootType = ReadByte(nbt, ref p);
        if (rootType == 0) return; // TAG_End as root → empty
        SkipName(nbt, ref p);

        // Each frame is an open container: a compound (read named tags until TAG_End) or a list
        // (read N values of one element type). Stack depth == nesting depth.
        var stack = new List<Frame>();
        ReadValue(nbt, ref p, rootType, stack);
        while (stack.Count > 0)
        {
            Frame f = stack[^1];
            if (f.IsList)
            {
                if (f.Remaining <= 0) { stack.RemoveAt(stack.Count - 1); continue; }
                f.Remaining--;
                stack[^1] = f;
                ReadValue(nbt, ref p, f.ElemType, stack);
            }
            else
            {
                byte t = ReadByte(nbt, ref p);
                if (t == 0) { stack.RemoveAt(stack.Count - 1); continue; } // TAG_End closes the compound
                SkipName(nbt, ref p);
                ReadValue(nbt, ref p, t, stack);
            }
        }
    }

    private struct Frame { public bool IsList; public byte ElemType; public int Remaining; }

    private static void ReadValue(ReadOnlySpan<byte> b, ref int p, byte type, List<Frame> stack)
    {
        switch (type)
        {
            case 1: Skip(b, ref p, 1); break;                                            // Byte
            case 2: Skip(b, ref p, 2); break;                                            // Short
            case 3: Skip(b, ref p, 4); break;                                            // Int
            case 4: Skip(b, ref p, 8); break;                                            // Long
            case 5: Skip(b, ref p, 4); break;                                            // Float
            case 6: Skip(b, ref p, 8); break;                                            // Double
            case 7: SkipArray(b, ref p, ReadInt(b, ref p), 1); break;                    // ByteArray
            case 8: Skip(b, ref p, ReadUShort(b, ref p)); break;                         // String
            case 11: SkipArray(b, ref p, ReadInt(b, ref p), 4); break;                   // IntArray
            case 12: SkipArray(b, ref p, ReadInt(b, ref p), 8); break;                   // LongArray
            case 9:                                                                      // List
                {
                    byte et = ReadByte(b, ref p);
                    int count = ReadInt(b, ref p);
                    if (count < 0) throw Bad("negative list length");
                    Push(stack, new Frame { IsList = true, ElemType = et, Remaining = count });
                    break;
                }
            case 10: Push(stack, new Frame { IsList = false }); break;                    // Compound
            default: throw Bad($"unknown NBT tag type {type}");
        }
    }

    private static void Push(List<Frame> stack, Frame f)
    {
        if (stack.Count + 1 > MaxDepth) throw Bad($"NBT nesting exceeds {MaxDepth}");
        stack.Add(f);
    }

    private static byte ReadByte(ReadOnlySpan<byte> b, ref int p) { Need(b, p, 1); return b[p++]; }
    private static int ReadUShort(ReadOnlySpan<byte> b, ref int p) { Need(b, p, 2); int v = BinaryPrimitives.ReadUInt16BigEndian(b[p..]); p += 2; return v; }
    private static int ReadInt(ReadOnlySpan<byte> b, ref int p) { Need(b, p, 4); int v = BinaryPrimitives.ReadInt32BigEndian(b[p..]); p += 4; return v; }
    private static void SkipName(ReadOnlySpan<byte> b, ref int p) => Skip(b, ref p, ReadUShort(b, ref p));

    private static void Skip(ReadOnlySpan<byte> b, ref int p, int n)
    {
        if (n < 0) throw Bad("negative length");
        Need(b, p, n);
        p += n;
    }

    private static void SkipArray(ReadOnlySpan<byte> b, ref int p, int count, int elemSize)
    {
        if (count < 0) throw Bad("negative array length");
        long bytes = (long)count * elemSize;
        if (p + bytes > b.Length) throw Bad("truncated NBT array");
        p += (int)bytes;
    }

    private static void Need(ReadOnlySpan<byte> b, int p, int n)
    {
        if (p + (long)n > b.Length) throw Bad("truncated NBT");
    }

    private static InvalidDataException Bad(string msg) => new($"invalid NBT: {msg}");
}
