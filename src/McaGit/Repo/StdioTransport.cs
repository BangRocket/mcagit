using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McaGit.Repo;

/// <summary>Length-prefixed (4-byte BE) framing over a byte stream, for the ssh stdio protocol.</summary>
internal static class Frame
{
    public static void Write(Stream s, byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        s.Write(len);
        s.Write(payload);
        s.Flush();
    }

    private const int MaxFrame = 256 * 1024 * 1024; // cap a peer-supplied frame length

    public static byte[]? Read(Stream s)
    {
        Span<byte> len = stackalloc byte[4];
        if (!ReadFully(s, len)) return null; // clean EOF
        int n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n < 0 || n > MaxFrame) throw new InvalidDataException($"frame length out of range: {n}");
        var buf = new byte[n];
        if (n > 0 && !ReadFully(s, buf)) throw new EndOfStreamException("truncated frame");
        return buf;
    }

    private static bool ReadFully(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf[off..]);
            if (r == 0) return off == 0 ? false : throw new EndOfStreamException();
            off += r;
        }
        return true;
    }
}

internal sealed class StdioCmd
{
    public string Op { get; set; } = "";
    public List<string>? Hashes { get; set; }
    public string? Hash { get; set; }
    public string? Branch { get; set; }
    public string? Old { get; set; }
    [JsonPropertyName("new")] public string? New { get; set; }
    public bool Force { get; set; }
}

internal sealed class StdioResp
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public RefAdvertisement? Refs { get; set; }
    public List<string>? Missing { get; set; }
}

/// <summary>Serves the remote protocol over a pair of byte streams (ssh stdin/stdout).</summary>
public static class StdioServer
{
    public static void Serve(RemoteService svc, Stream input, Stream output)
    {
        byte[]? frame;
        while ((frame = Frame.Read(input)) is not null)
        {
            StdioCmd cmd = JsonSerializer.Deserialize<StdioCmd>(frame, HttpProtocol.Json)!;
            try
            {
                switch (cmd.Op)
                {
                    case "list": Respond(output, new StdioResp { Refs = svc.ListRefs() }); break;
                    case "missing": Respond(output, new StdioResp { Missing = svc.Missing(cmd.Hashes ?? []).ToList() }); break;
                    case "get":
                        byte[] obj = svc.GetObject(cmd.Hash!);
                        Respond(output, new StdioResp { Ok = true });
                        Frame.Write(output, obj);
                        break;
                    case "put":
                        byte[] blob = Frame.Read(input) ?? throw new EndOfStreamException("missing object payload");
                        svc.PutObject(cmd.Hash!, blob);
                        Respond(output, new StdioResp { Ok = true });
                        break;
                    case "put-pack":
                        byte[] idx = Frame.Read(input) ?? throw new EndOfStreamException("missing pack index");
                        byte[] pack = Frame.Read(input) ?? throw new EndOfStreamException("missing pack payload");
                        svc.PutPack(pack, idx);
                        Respond(output, new StdioResp { Ok = true });
                        break;
                    case "ref":
                        svc.UpdateRef(cmd.Branch!, cmd.Old, cmd.New!, cmd.Force);
                        Respond(output, new StdioResp { Ok = true });
                        break;
                    default: Respond(output, new StdioResp { Ok = false, Error = "unknown op" }); break;
                }
            }
            catch (Exception e) { Respond(output, new StdioResp { Ok = false, Error = e.Message }); }
        }
    }

    private static void Respond(Stream output, StdioResp r)
        => Frame.Write(output, JsonSerializer.SerializeToUtf8Bytes(r, HttpProtocol.Json));
}

/// <summary>Client transport over a pair of byte streams (used by ssh).</summary>
public sealed class StdioTransport(Stream send, Stream recv) : IRemoteTransport, IBatchTransport
{
    public RefAdvertisement ListRefs() => Do(new StdioCmd { Op = "list" }).Refs!;

    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes)
        => Do(new StdioCmd { Op = "missing", Hashes = hashes.ToList() }).Missing ?? [];

    public byte[] GetObject(string hash)
    {
        Do(new StdioCmd { Op = "get", Hash = hash });
        return Frame.Read(recv) ?? throw new EndOfStreamException("missing object payload");
    }

    public void PutObject(string hash, byte[] compressed)
    {
        Frame.Write(send, JsonSerializer.SerializeToUtf8Bytes(new StdioCmd { Op = "put", Hash = hash }, HttpProtocol.Json));
        Frame.Write(send, compressed);
        Check();
    }

    public void PutObjects(IReadOnlyList<(string Hash, byte[] Content)> objects)
    {
        if (PackTransfer.Build(objects) is not { } p) return;
        Frame.Write(send, JsonSerializer.SerializeToUtf8Bytes(new StdioCmd { Op = "put-pack" }, HttpProtocol.Json));
        Frame.Write(send, p.Idx);   // server reads idx then pack — keep this order
        Frame.Write(send, p.Pack);
        Check();
    }

    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
        => Do(new StdioCmd { Op = "ref", Branch = branch, Old = expectedOld, New = newHash, Force = force });

    public void Dispose() { }

    private StdioResp Do(StdioCmd cmd)
    {
        Frame.Write(send, JsonSerializer.SerializeToUtf8Bytes(cmd, HttpProtocol.Json));
        return ReadResp();
    }

    private void Check() => ReadResp();

    private StdioResp ReadResp()
    {
        byte[] r = Frame.Read(recv) ?? throw new EndOfStreamException("remote closed");
        StdioResp resp = JsonSerializer.Deserialize<StdioResp>(r, HttpProtocol.Json)!;
        if (!resp.Ok) throw new InvalidOperationException($"remote: {resp.Error}");
        return resp;
    }
}
