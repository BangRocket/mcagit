using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

public class NetworkTests
{
    // ---- HTTP ----

    [Fact]
    public void Http_Push_Clone_RoundTrip_AndAuth()
    {
        Repository a = Repository.Init(TestAnvil.TempDir("hA"));
        CommitWorld(a, World("a"), "c1");
        CommitWorld(a, World("b"), "c2");
        string main = a.ReadBranch("main")!;

        string remoteDir = TestAnvil.TempDir("hR");
        Repository.Init(remoteDir);
        int port = FreePort();
        var server = new RepoServer(Repository.Open(remoteDir), allowPush: true, token: "T");
        server.Start("localhost", port);
        var thread = new Thread(server.Run) { IsBackground = true };
        thread.Start();
        try
        {
            string url = $"http://localhost:{port}";

            // push without token → rejected
            Assert.ThrowsAny<Exception>(() => RemoteOps.Push(a, url, "main", force: false, token: null));

            // push with token → remote advances
            RemoteOps.Push(a, url, "main", force: false, token: "T");
            Assert.Equal(main, Repository.Open(remoteDir).ReadBranch("main"));

            // anonymous clone over HTTP → same tip
            string bDir = TestAnvil.TempDir("hB");
            RemoteOps.Clone(url, bDir, token: null);
            Assert.Equal(main, Repository.Open(bDir).ReadBranch("main"));
        }
        finally { server.Stop(); }
    }

    // ---- ssh stdio protocol (over in-memory pipes; no real ssh needed) ----

    [Fact]
    public void Stdio_Clone_RoundTrip()
    {
        Repository remote = Repository.Init(TestAnvil.TempDir("sR"));
        CommitWorld(remote, World("a"), "c1");
        string main = remote.ReadBranch("main")!;

        WithStdioServer(remote, allowWrite: false, transport =>
        {
            string bDir = TestAnvil.TempDir("sB");
            RemoteOps.CloneFrom(transport, bDir, "ssh://test/repo");
            Assert.Equal(main, Repository.Open(bDir).ReadBranch("main"));
        });
    }

    [Fact]
    public void Stdio_Push_RoundTrip()
    {
        Repository a = Repository.Init(TestAnvil.TempDir("spA"));
        CommitWorld(a, World("a"), "c1");
        string main = a.ReadBranch("main")!;
        Repository remote = Repository.Init(TestAnvil.TempDir("spR"));

        WithStdioServer(remote, allowWrite: true, transport =>
            RemoteOps.PushTo(a, transport, "main", force: false));

        Assert.Equal(main, Repository.Open(remote.Dir).ReadBranch("main"));
    }

    // ---- helpers ----

    private static void WithStdioServer(Repository repo, bool allowWrite, Action<IRemoteTransport> body)
    {
        // client -> server and server -> client byte channels
        using var c2s = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, c2s.GetClientHandleAsString());
        using var s2c = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, s2c.GetClientHandleAsString());

        var svc = new RemoteService(repo, allowWrite);
        var thread = new Thread(() => StdioServer.Serve(svc, serverIn, s2c)) { IsBackground = true };
        thread.Start();
        try
        {
            using var transport = new StdioTransport(c2s, clientIn);
            body(transport);
        }
        finally
        {
            c2s.Close();          // EOF → server loop exits
            thread.Join(2000);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string World(string tag)
    {
        string dir = TestAnvil.TempDir("nw-" + tag);
        var root = TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtString("Status", tag));
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), root));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
