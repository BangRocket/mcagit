using McaGit.Output;
using Xunit;

namespace McaGit.Tests;

/// <summary>The git-style progress reporter: format (percent / indeterminate / ", done."), and that a
/// disabled instance is a complete no-op (so progress never pollutes piped/CI/--json output).</summary>
public class ProgressTests
{
    private static string Capture(bool on, Action<Progress> act)
    {
        TextWriter prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { act(new Progress(on)); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        Assert.Equal("", Capture(on: false, p => { p.Begin("Snapshotting world"); p.Update(1, 2); p.Done(2, 2, "x"); }));
    }

    [Fact]
    public void Done_RendersPercentAndDone_InPlace()
    {
        string o = Capture(on: true, p => { p.Begin("Snapshotting world"); p.Done(870, 870, "309870 chunks"); });
        Assert.Contains("Snapshotting world: 100% (870/870), 309870 chunks, done.", o);
        Assert.StartsWith("\r", o);   // repaints in place
        Assert.EndsWith("\n", o);     // a finished phase drops to the next line
    }

    [Fact]
    public void Update_PaintsFirstTick_WithRightAlignedPercent()
    {
        string o = Capture(on: true, p => { p.Begin("Checking out"); p.Update(1, 4); });
        Assert.Contains("Checking out:  25% (1/4)", o); // 3-wide percent, no trailing newline mid-phase
        Assert.DoesNotContain("done", o);
    }

    [Fact]
    public void IndeterminateTotal_OmitsPercent()
    {
        string o = Capture(on: true, p => { p.Begin("Counting objects"); p.Done(42, 0); });
        Assert.Contains("Counting objects: 42, done.", o);
        Assert.DoesNotContain("%", o);
    }
}
