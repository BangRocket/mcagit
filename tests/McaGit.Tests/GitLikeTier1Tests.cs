using System.Diagnostics;
using McaGit.Anvil;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>Tier 1 git-likeness: config/identity, committer split, annotated &amp;
/// SSH-signed tags, object classification, plumbing primitives, and fsck.</summary>
public class GitLikeTier1Tests
{
    // ---- config & identity ----

    [Fact]
    public void Config_RepoLevel_SetGetUnset()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cfg"));
        Assert.Null(repo.GetConfig("user.name"));
        repo.SetConfig("user.name", "Ada", global: false);
        repo.SetConfig("user.email", "ada@x.dev", global: false);
        Assert.Equal("Ada", repo.GetConfig("user.name"));
        Assert.Contains(("user.email", "ada@x.dev", false), repo.ListConfig());

        Assert.True(repo.UnsetConfig("user.name", global: false));
        Assert.Null(repo.GetConfig("user.name"));
        Assert.False(repo.UnsetConfig("user.name", global: false));
    }

    [Fact]
    public void ConfiguredIdentity_FormatsNameAndEmail()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("id"));
        Assert.Null(repo.ConfiguredIdentity());
        repo.SetConfig("user.name", "Ada Lovelace", global: false);
        Assert.Equal("Ada Lovelace", repo.ConfiguredIdentity());
        repo.SetConfig("user.email", "ada@x.dev", global: false);
        Assert.Equal("Ada Lovelace <ada@x.dev>", repo.ConfiguredIdentity());
    }

    // ---- author / committer split ----

    [Fact]
    public void CreateCommit_DefaultsCommitterToAuthor_AndStampsDates()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("auth"));
        string tree = repo.WriteManifest(new Manifest());
        string c = repo.CreateCommit(tree, [], "m", "Ada <a@x>");
        CommitObject co = repo.ReadCommit(c);
        Assert.Equal("Ada <a@x>", co.Author);
        Assert.Equal("Ada <a@x>", co.CommitterOrAuthor);
        Assert.False(string.IsNullOrEmpty(co.CommitDate));
    }

    [Fact]
    public void CreateCommit_PreservesAuthor_WhileRecordingCommitter()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("auth2"));
        string tree = repo.WriteManifest(new Manifest());
        string c = repo.CreateCommit(tree, [], "cherry", "Orig <o@x>",
            committer: "Replayer <r@x>", authorTime: "2020-01-01T00:00:00.0000000+00:00");
        CommitObject co = repo.ReadCommit(c);
        Assert.Equal("Orig <o@x>", co.Author);
        Assert.Equal("Replayer <r@x>", co.Committer);
        Assert.Equal("2020-01-01T00:00:00.0000000+00:00", co.Time);
        Assert.NotEqual(co.Time, co.CommitTime);
    }

    // ---- annotated tags & object classification ----

    [Fact]
    public void AnnotatedTag_PeelsToCommit_AndClassifies()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("atag"));
        string c = CommitWorld(repo, World("a"), "c0");
        var tag = new TagObject { Object = c, Type = "commit", Tag = "v1", Tagger = "t <t@x>", Time = "2020", Message = "release one" };
        string th = repo.WriteAnnotatedTag(tag);

        Assert.Equal(Repository.ObjectKind.Tag, repo.Classify(th));
        Assert.Equal(c, repo.PeelToCommit(th));
        Assert.Equal(c, repo.ResolveRef("v1"));               // resolves through the tag object
        Assert.Equal("release one", repo.ReadAnnotatedTag("v1")!.Message);
        Assert.Null(repo.ReadAnnotatedTag("nope"));
    }

    [Fact]
    public void LightweightTag_IsNotAnnotated()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("ltag"));
        string c = CommitWorld(repo, World("a"), "c0");
        repo.WriteTag("v0", c);
        Assert.Null(repo.ReadAnnotatedTag("v0"));             // ref holds the commit directly
        Assert.Equal(c, repo.ResolveRef("v0"));
    }

    [Fact]
    public void Classify_DistinguishesCommitTreeBlobTag()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cls"));
        string c = CommitWorld(repo, World("a"), "c0");
        string tree = repo.ReadCommit(c).Tree;
        string chunk = repo.ReadManifest(tree).Regions["region/r.0.0.mca"]["0,0"];
        string blob = repo.Objects.Write([1, 2, 3, 4]);
        string th = repo.WriteAnnotatedTag(new TagObject { Object = c, Tag = "t", Tagger = "x", Message = "m" });

        Assert.Equal(Repository.ObjectKind.Commit, repo.Classify(c));
        Assert.Equal(Repository.ObjectKind.Tree, repo.Classify(tree));
        Assert.Equal(Repository.ObjectKind.Blob, repo.Classify(chunk)); // canonical NBT is binary
        Assert.Equal(Repository.ObjectKind.Blob, repo.Classify(blob));
        Assert.Equal(Repository.ObjectKind.Tag, repo.Classify(th));
    }

    // ---- plumbing primitive: hash-object must agree with the store ----

    [Fact]
    public void HashObject_MatchesStoreHash()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("ho"));
        byte[] bytes = [9, 8, 7, 6, 5];
        string stored = repo.Objects.Write(bytes);
        string computed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.Equal(stored, computed); // `hash-object` (no -w) prints exactly this
    }

    // ---- fsck ----

    [Fact]
    public void Fsck_HealthyRepo_IsOk()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("fk"));
        CommitWorld(repo, World("a"), "c0");
        CommitWorld(repo, World("b"), "c1");
        Fsck.Report r = Fsck.Check(repo);
        Assert.True(r.Ok);
        Assert.Empty(r.Corrupt);
        Assert.Empty(r.Missing);
        Assert.True(r.Checked > 0);
    }

    [Fact]
    public void Fsck_DetectsCorruptObject()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("fkc"));
        string c = CommitWorld(repo, World("a"), "c0");
        // Corrupt the commit object on disk so it no longer decompresses to its hash.
        File.WriteAllBytes(Path.Combine(repo.Dir, "objects", c[..2], c[2..]), [0xDE, 0xAD, 0xBE, 0xEF]);

        Assert.False(repo.Objects.VerifyIntegrity(c));
        Fsck.Report r = Fsck.Check(repo);
        Assert.False(r.Ok);
        Assert.Contains(c, r.Corrupt);
    }

    [Fact]
    public void Fsck_DetectsMissingObject()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("fkm"));
        string c = CommitWorld(repo, World("a"), "c0");
        string chunk = repo.ReadManifest(repo.ReadCommit(c).Tree).Regions["region/r.0.0.mca"]["0,0"];
        File.Delete(Path.Combine(repo.Dir, "objects", chunk[..2], chunk[2..])); // referenced, now absent

        Fsck.Report r = Fsck.Check(repo);
        Assert.False(r.Ok);
        Assert.Contains(r.Missing, m => m.StartsWith(chunk));
    }

    // ---- SSH signing (only when ssh-keygen is present) ----

    [Fact]
    public void SshSign_And_Verify_RoundTrips_WhenAvailable()
    {
        if (!SshSigner.Available) return; // environment without ssh-keygen — nothing to test
        string dir = TestAnvil.TempDir("ssh");
        string key = Path.Combine(dir, "id_ed25519");
        if (!GenerateKey(key)) return; // key generation unavailable/blocked — skip

        const string payload = "mcagit signs this";
        string sig = SshSigner.Sign(payload, key);
        Assert.Contains("BEGIN SSH SIGNATURE", sig);

        Assert.True(SshSigner.Verify(payload, sig, null).Valid);          // correct payload
        Assert.False(SshSigner.Verify("tampered", sig, null).Valid);      // wrong payload

        // End-to-end: a signed commit verifies against its own signable payload.
        Repository repo = Repository.Init(TestAnvil.TempDir("sshrepo"));
        repo.SetConfig("user.signingkey", key, global: false);
        string tree = repo.WriteManifest(new Manifest());
        string c = repo.CreateCommit(tree, [], "signed", "Ada <a@x>",
            sign: p => SshSigner.Sign(p, key));
        CommitObject co = repo.ReadCommit(c);
        Assert.NotNull(co.Signature);
        Assert.True(SshSigner.Verify(co.SignablePayload(), co.Signature!, null).Valid);
    }

    [Fact]
    public void TagVerify_ValidButUntrustedSigner_ExitsOne_WhenAvailable()
    {
        if (!SshSigner.Available) return;
        string dir = TestAnvil.TempDir("sshv");
        string key = Path.Combine(dir, "id_ed25519");
        if (!GenerateKey(key)) return;

        Repository repo = Repository.Init(TestAnvil.TempDir("sshvr"));
        repo.SetConfig("user.signingkey", key, global: false);
        repo.SetConfig("user.name", "Ada", global: false);
        repo.SetConfig("user.email", "a@x", global: false);
        string c = repo.CreateCommit(repo.WriteManifest(new Manifest()), [], "c0", "Ada <a@x>");
        repo.WriteBranch("main", c);
        repo.SetHeadToBranch("main");

        Assert.Equal(0, Cli.RepoCommands.Tag(repo.Dir, ["-a", "v1", "-m", "release", "-s"])); // signed tag

        // The signature is cryptographically valid on its own...
        TagObject t = repo.ReadAnnotatedTag("v1")!;
        Assert.True(SshSigner.Verify(t.SignablePayload(), t.Signature!, null).Valid);
        // ...but with no gpg.ssh.allowedSignersFile the signer is untrusted, so `tag -v` must exit 1,
        // not 0 — exit 0 would fool a `tag -v … && deploy` gate (issue #24).
        Assert.Equal(1, Cli.RepoCommands.Tag(repo.Dir, ["-v", "v1"]));
    }

    private static bool GenerateKey(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh-keygen")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "mcagit-test", "-f", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && File.Exists(path);
        }
        catch { return false; }
    }

    // ---- helpers ----

    private static string World(string tag)
    {
        string dir = TestAnvil.TempDir("w-" + tag);
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), TestAnvil.Root(new fNbt.NbtInt("DataVersion", 3953), new fNbt.NbtString("Status", tag))));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
