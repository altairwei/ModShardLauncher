using System.Linq;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 3] FinalTextStore：本轮 Save 记账，last-writer-wins。纯静态，不拉 vanilla 图。</summary>
public class FinalTextStoreTests : IDisposable
{
    public void Dispose() => FinalTextStore.ResetRound();

    [Fact]
    public void RecordTwice_LastWriterWins()
    {
        FinalTextStore.Record("scr_a", PatchingWay.GML, "v1");
        FinalTextStore.Record("scr_a", PatchingWay.GML, "v2");
        Assert.True(FinalTextStore.TryGet("scr_a", out var text, out var way));
        Assert.Equal("v2", text);
        Assert.Equal(PatchingWay.GML, way);
    }

    [Fact]
    public void ResetRound_Clears()
    {
        FinalTextStore.Record("scr_a", PatchingWay.GML, "v1");
        FinalTextStore.ResetRound();
        Assert.Equal(0, FinalTextStore.Count);
        Assert.False(FinalTextStore.Contains("scr_a"));
    }

    [Fact]
    public void Entries_Snapshot()
    {
        FinalTextStore.Record("scr_a", PatchingWay.GML, "v1");
        FinalTextStore.Record("scr_b", PatchingWay.AssemblyAsString, "v2");
        var list = FinalTextStore.Entries.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(("scr_a", PatchingWay.GML, "v1"), list);
        Assert.Contains(("scr_b", PatchingWay.AssemblyAsString, "v2"), list);
    }
}
