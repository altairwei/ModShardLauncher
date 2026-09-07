using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 3] CompileLedger：entry → (way, textHash) 记账语义。纯静态，不拉 vanilla 图。</summary>
public class CompileLedgerTests : IDisposable
{
    public void Dispose() => CompileLedger.Clear();

    [Fact]
    public void MarkThenIsCurrent_True_ForSameHash()
    {
        string h = TextHash.Hash("return 1;");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, h);
        Assert.True(CompileLedger.IsCurrent("scr_a", h));
        Assert.False(CompileLedger.IsCurrent("scr_a", TextHash.Hash("return 2;")));
        Assert.False(CompileLedger.IsCurrent("scr_b", h));
    }

    [Fact]
    public void TryGet_ReturnsWayAndHash()
    {
        string h = TextHash.Hash("x");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.AssemblyAsString, h);
        Assert.True(CompileLedger.TryGet("scr_a", out var way, out var hash));
        Assert.Equal(PatchingWay.AssemblyAsString, way);
        Assert.Equal(h, hash);
    }

    [Fact]
    public void Clear_EmptiesLedger()
    {
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, TextHash.Hash("x"));
        Assert.Equal(1, CompileLedger.Count);
        Assert.True(CompileLedger.Contains("scr_a"));
        CompileLedger.Clear();
        Assert.Equal(0, CompileLedger.Count);
        Assert.False(CompileLedger.Contains("scr_a"));
        Assert.False(CompileLedger.TryGet("scr_a", out _, out _));
    }

    [Fact]
    public void Entries_Snapshot_AllMarked()
    {
        string h1 = TextHash.Hash("a");
        string h2 = TextHash.Hash("b");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, h1);
        CompileLedger.MarkCompiled("scr_b", PatchingWay.AssemblyAsString, h2);
        var list = CompileLedger.Entries.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(("scr_a", PatchingWay.GML, h1), list);
        Assert.Contains(("scr_b", PatchingWay.AssemblyAsString, h2), list);
    }
}
