using System.Net;
using System.Text;
using System.Text.Json;

namespace McaDiff.Repo;

/// <summary>
/// Built-in HTTP server exposing a repository to network remotes (<c>mcadiff serve</c>).
/// Reads (clone/fetch) are anonymous; writes (push) require <c>allowPush</c> and,
/// if a <c>token</c> is configured, a matching <c>Authorization</c> header — the
/// "anonymous read, authenticated push" model git uses over HTTP.
/// </summary>
public sealed class RepoServer
{
    private readonly RemoteService _svc;
    private readonly bool _allowPush;
    private readonly string? _token;
    private readonly HttpListener _listener = new();

    public RepoServer(Repository repo, bool allowPush, string? token)
    {
        _svc = new RemoteService(repo, allowPush);
        _allowPush = allowPush;
        _token = token;
    }

    public void Start(string host, int port)
    {
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();
    }

    public void Stop() { try { _listener.Stop(); _listener.Close(); } catch { } }

    public void Run()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; } // listener stopped
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url!.AbsolutePath;
            string method = req.HttpMethod;

            if (method == "GET" && path == "/info/refs") WriteJson(ctx, _svc.ListRefs());
            else if (method == "POST" && path == "/have") WriteJson(ctx, _svc.Missing(ReadJson<List<string>>(req)));
            else if (method == "GET" && path.StartsWith("/objects/"))
            {
                try { WriteBytes(ctx, _svc.GetObject(path["/objects/".Length..])); }
                catch (IOException) { ctx.Response.StatusCode = 404; }
            }
            else if (method == "POST" && path.StartsWith("/objects/"))
            {
                if (!Authorize(ctx)) return;
                _svc.PutObject(path["/objects/".Length..], ReadBytes(req));
            }
            else if (method == "POST" && path == "/pack")
            {
                if (!Authorize(ctx)) return;
                (byte[] pack, byte[] idx) = PackTransfer.UnframeBody(ReadBytes(req));
                _svc.PutPack(pack, idx);
            }
            else if (method == "POST" && path.StartsWith("/refs/heads/"))
            {
                if (!Authorize(ctx)) return;
                RefUpdate u = ReadJson<RefUpdate>(req);
                _svc.UpdateRef(path["/refs/heads/".Length..], u.Old, u.New, u.Force);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch (UnauthorizedAccessException e) { Fail(ctx, 403, e.Message); }
        catch (Exception e) { Fail(ctx, 400, e.Message); }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private bool Authorize(HttpListenerContext ctx)
    {
        if (!_allowPush) { Fail(ctx, 403, "this server is read-only (start with --allow-push)"); return false; }
        if (_token is not null && !TokenMatches(ctx.Request.Headers["Authorization"], _token))
        {
            Fail(ctx, 401, "missing or invalid token");
            return false;
        }
        return true;
    }

    private static bool TokenMatches(string? header, string token)
    {
        if (string.IsNullOrEmpty(header)) return false;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return ConstantTimeEquals(header["Bearer ".Length..].Trim(), token);
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                int colon = decoded.IndexOf(':');
                if (colon < 0) return false; // RFC 7617: user:pass — no colon is malformed
                return ConstantTimeEquals(decoded[(colon + 1)..], token); // password field
            }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Compares token bytes without an early-out, so response timing can't leak the token.</summary>
    private static bool ConstantTimeEquals(string a, string b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static void WriteJson(HttpListenerContext ctx, object value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, HttpProtocol.Json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(body);
    }

    private static void WriteBytes(HttpListenerContext ctx, byte[] bytes)
    {
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.OutputStream.Write(bytes);
    }

    private static void Fail(HttpListenerContext ctx, int code, string message)
    {
        ctx.Response.StatusCode = code;
        try { ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes(message)); } catch { }
    }

    /// <summary>Largest request body we'll buffer (anonymous /have and authenticated PUT).</summary>
    private const long MaxBody = 256L * 1024 * 1024;

    private static T ReadJson<T>(HttpListenerRequest req)
        => JsonSerializer.Deserialize<T>(ReadBytes(req), HttpProtocol.Json)!;

    private static byte[] ReadBytes(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBody) throw new InvalidDataException("request body too large");
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        long total = 0;
        int r;
        while ((r = req.InputStream.Read(buf, 0, buf.Length)) > 0)
        {
            total += r;
            if (total > MaxBody) throw new InvalidDataException("request body too large"); // also bounds chunked encoding
            ms.Write(buf, 0, r);
        }
        return ms.ToArray();
    }
}
