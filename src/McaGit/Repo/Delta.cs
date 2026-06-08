namespace McaGit.Repo;

/// <summary>
/// Binary delta between two byte buffers, using git's copy/insert opcode format so
/// near-identical objects (e.g. a chunk whose only change is a ticking
/// <c>InhabitedTime</c>) pack to a handful of bytes. <see cref="Diff"/> finds shared
/// runs with a windowed rolling index; <see cref="Apply"/> reconstructs the target.
/// </summary>
public static class Delta
{
    private const int Window = 16;        // min match length worth a copy
    private const int MaxCopy = 0xFFFFFF; // 24-bit copy size ceiling

    public static byte[] Diff(byte[] baseBuf, byte[] target)
    {
        var ms = new MemoryStream();
        WriteVarint(ms, (ulong)baseBuf.Length);
        WriteVarint(ms, (ulong)target.Length);

        // Index base windows by a content hash → earliest offsets (capped, to bound work).
        var index = new Dictionary<ulong, List<int>>();
        for (int i = 0; i + Window <= baseBuf.Length; i++)
        {
            ulong h = HashWindow(baseBuf, i);
            if (!index.TryGetValue(h, out List<int>? list)) index[h] = list = new(4);
            if (list.Count < 4) list.Add(i);
        }

        var insert = new List<byte>();
        int p = 0;
        while (p < target.Length)
        {
            int bestOff = -1, bestLen = 0;
            if (p + Window <= target.Length && index.TryGetValue(HashWindow(target, p), out List<int>? cands))
                foreach (int off in cands)
                {
                    int len = MatchLength(baseBuf, off, target, p);
                    if (len > bestLen) { bestLen = len; bestOff = off; }
                }

            if (bestLen >= Window)
            {
                FlushInsert(ms, insert);
                EmitCopy(ms, bestOff, bestLen);
                p += bestLen;
            }
            else { insert.Add(target[p]); p++; }
        }
        FlushInsert(ms, insert);
        return ms.ToArray();
    }

    public static byte[] Apply(byte[] baseBuf, byte[] delta)
    {
        int dp = 0;
        ulong baseLen = ReadVarint(delta, ref dp);
        ulong targetLen = ReadVarint(delta, ref dp);
        if (baseLen != (ulong)baseBuf.Length) throw new InvalidDataException("delta base size mismatch");

        var outBuf = new byte[targetLen];
        int op = 0;
        while (dp < delta.Length)
        {
            byte cmd = delta[dp++];
            if ((cmd & 0x80) != 0) // copy from base
            {
                int offset = 0, size = 0;
                if ((cmd & 0x01) != 0) offset |= delta[dp++];
                if ((cmd & 0x02) != 0) offset |= delta[dp++] << 8;
                if ((cmd & 0x04) != 0) offset |= delta[dp++] << 16;
                if ((cmd & 0x08) != 0) offset |= delta[dp++] << 24;
                if ((cmd & 0x10) != 0) size |= delta[dp++];
                if ((cmd & 0x20) != 0) size |= delta[dp++] << 8;
                if ((cmd & 0x40) != 0) size |= delta[dp++] << 16;
                if (size == 0) size = 0x10000;
                // Bounds-check before copying: a malformed/hostile delta (incl. a byte-3 offset
                // that sign-extends negative) must fail catchably, not throw ArgumentOutOfRange.
                if (offset < 0 || size < 0 || (long)offset + size > baseBuf.Length || op + size > outBuf.Length)
                    throw new InvalidDataException("delta copy out of range");
                Array.Copy(baseBuf, offset, outBuf, op, size);
                op += size;
            }
            else if (cmd != 0) // insert literal
            {
                if (dp + cmd > delta.Length || op + cmd > outBuf.Length)
                    throw new InvalidDataException("delta insert out of range");
                Array.Copy(delta, dp, outBuf, op, cmd);
                dp += cmd;
                op += cmd;
            }
            else throw new InvalidDataException("invalid delta opcode 0x00");
        }
        if (op != (int)targetLen) throw new InvalidDataException("delta produced wrong length");
        return outBuf;
    }

    private static int MatchLength(byte[] b, int bi, byte[] t, int ti)
    {
        int max = Math.Min(b.Length - bi, t.Length - ti);
        int k = 0;
        while (k < max && b[bi + k] == t[ti + k]) k++;
        return k;
    }

    private static ulong HashWindow(byte[] buf, int at)
    {
        // FNV-1a over a fixed window — cheap, and copies are byte-verified anyway.
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < Window; i++) { h ^= buf[at + i]; h *= 1099511628211UL; }
        return h;
    }

    private static void EmitCopy(Stream s, int offset, int length)
    {
        Span<byte> buf = stackalloc byte[7];
        while (length > 0)
        {
            int chunk = Math.Min(length, MaxCopy);
            byte cmd = 0x80;
            int n = 0;
            if ((offset & 0xFF) != 0) { cmd |= 0x01; buf[n++] = (byte)offset; }
            if ((offset & 0xFF00) != 0) { cmd |= 0x02; buf[n++] = (byte)(offset >> 8); }
            if ((offset & 0xFF0000) != 0) { cmd |= 0x04; buf[n++] = (byte)(offset >> 16); }
            if (((uint)offset & 0xFF000000) != 0) { cmd |= 0x08; buf[n++] = (byte)(offset >> 24); }
            if ((chunk & 0xFF) != 0) { cmd |= 0x10; buf[n++] = (byte)chunk; }
            if ((chunk & 0xFF00) != 0) { cmd |= 0x20; buf[n++] = (byte)(chunk >> 8); }
            if ((chunk & 0xFF0000) != 0) { cmd |= 0x40; buf[n++] = (byte)(chunk >> 16); }
            s.WriteByte(cmd);
            s.Write(buf[..n]);
            offset += chunk;
            length -= chunk;
        }
    }

    private static void FlushInsert(Stream s, List<byte> insert)
    {
        int i = 0;
        while (i < insert.Count)
        {
            int n = Math.Min(127, insert.Count - i);
            s.WriteByte((byte)n);
            for (int k = 0; k < n; k++) s.WriteByte(insert[i + k]);
            i += n;
        }
        insert.Clear();
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static ulong ReadVarint(byte[] buf, ref int p)
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            byte b = buf[p++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }
}
