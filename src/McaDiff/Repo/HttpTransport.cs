using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace McaDiff.Repo;

/// <summary>Client transport over HTTP to a <c>mcadiff serve</c> endpoint.</summary>
public sealed class HttpTransport : IRemoteTransport
{
    private readonly HttpClient _http = new();
    private readonly Uri _base;
    private readonly string? _token;

    public HttpTransport(string baseUrl, string? token)
    {
        _base = new Uri(baseUrl.TrimEnd('/') + "/");
        _token = token;
    }

    public RefAdvertisement ListRefs() => Json<RefAdvertisement>(Send(HttpMethod.Get, "info/refs", null));

    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes)
        => Json<List<string>>(Send(HttpMethod.Post, "have", JsonBody(hashes)));

    public byte[] GetObject(string hash) => Bytes(Send(HttpMethod.Get, "objects/" + hash, null));

    public void PutObject(string hash, byte[] compressed)
        => Send(HttpMethod.Post, "objects/" + hash, new ByteArrayContent(compressed)).Dispose();

    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
        => Send(HttpMethod.Post, "refs/heads/" + branch,
            JsonBody(new RefUpdate { Old = expectedOld, New = newHash, Force = force })).Dispose();

    public void Dispose() => _http.Dispose();

    private HttpResponseMessage Send(HttpMethod method, string path, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
        if (_token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        HttpResponseMessage resp = _http.Send(req);
        if (!resp.IsSuccessStatusCode)
        {
            string body = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            string hint = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " (set --token / MCADIFF_TOKEN, or the server must --allow-push)" : "";
            throw new InvalidOperationException($"remote HTTP {(int)resp.StatusCode}: {body}{hint}");
        }
        return resp;
    }

    private static byte[] Bytes(HttpResponseMessage r)
    {
        using var ms = new MemoryStream();
        r.Content.ReadAsStream().CopyTo(ms);
        r.Dispose();
        return ms.ToArray();
    }

    private static T Json<T>(HttpResponseMessage r)
    {
        using Stream s = r.Content.ReadAsStream();
        T value = JsonSerializer.Deserialize<T>(s, HttpProtocol.Json)!;
        r.Dispose();
        return value;
    }

    private static StringContent JsonBody(object o)
        => new(JsonSerializer.Serialize(o, HttpProtocol.Json), Encoding.UTF8, "application/json");
}
