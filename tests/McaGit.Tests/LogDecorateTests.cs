using McaGit.Cli;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>`log --decorate` ref labelling (issue #16 next-tier).</summary>
public class LogDecorateTests
{
    [Fact]
    public void DecorateRefs_LabelsBranchesTagsAndHead()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("dec"));
        string c0 = repo.CreateCommit(repo.WriteManifest(new Manifest()), [], "c0", "t");
        repo.WriteBranch("main", c0);
        repo.WriteBranch("dev", c0);
        repo.SetHeadToBranch("main");
        repo.WriteTag("v1", c0);

        Dictionary<string, List<string>> d = RepoCommands.DecorateRefs(repo);
        List<string> labels = Assert.Contains(c0, d);
        Assert.Contains("HEAD -> main", labels); // current branch gets the HEAD arrow
        Assert.Contains("dev", labels);
        Assert.Contains("tag: v1", labels);
    }
}
