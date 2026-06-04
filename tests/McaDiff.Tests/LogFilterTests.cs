using McaDiff.Cli;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>git-style `log` metadata filters (#16): --author / --grep / --merges / --no-merges /
/// --since / --until, AND-combined.</summary>
public class LogFilterTests
{
    private static CommitObject Commit(string author, string message, int parents, string time) =>
        new() { Author = author, Message = message, Parents = [.. Enumerable.Repeat("p", parents)], Time = time };

    [Fact]
    public void Author_Grep_AreCaseInsensitiveSubstrings()
    {
        CommitObject c = Commit("Alice <a@x>", "fix the crash bug", 1, "2026-06-04T00:00:00Z");
        Assert.True(Match(c, author: "alice"));
        Assert.True(Match(c, grep: "CRASH"));
        Assert.False(Match(c, author: "bob"));
        Assert.False(Match(c, grep: "feature"));
        Assert.True(Match(c, author: "alice", grep: "bug")); // AND: both pass
        Assert.False(Match(c, author: "alice", grep: "feature")); // AND: one fails
    }

    [Fact]
    public void Merges_And_NoMerges()
    {
        CommitObject normal = Commit("a", "m", 1, "2026-06-04T00:00:00Z");
        CommitObject merge = Commit("a", "m", 2, "2026-06-04T00:00:00Z");
        Assert.True(Match(merge, merges: true));
        Assert.False(Match(normal, merges: true));
        Assert.True(Match(normal, noMerges: true));
        Assert.False(Match(merge, noMerges: true));
    }

    [Fact]
    public void Since_Until_BoundByCommitTime()
    {
        CommitObject c = Commit("a", "m", 1, "2026-06-04T12:00:00Z");
        var before = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        var after = DateTimeOffset.Parse("2026-06-10T00:00:00Z");
        Assert.True(Match(c, since: before));    // after the since bound
        Assert.False(Match(c, since: after));    // before the since bound → excluded
        Assert.True(Match(c, until: after));     // before the until bound
        Assert.False(Match(c, until: before));   // after the until bound → excluded
        Assert.True(Match(c, since: before, until: after)); // within the window
    }

    private static bool Match(CommitObject c, string? author = null, string? grep = null,
        bool merges = false, bool noMerges = false, DateTimeOffset? since = null, DateTimeOffset? until = null)
        => RepoCommands.LogFilter(c, author, grep, merges, noMerges, since, until);
}
