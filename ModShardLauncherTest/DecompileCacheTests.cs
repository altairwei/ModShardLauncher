using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 2] DecompileCache：(entry, way) → vanilla 文本缓存。
/// 纯字典逻辑，不拉 vanilla 图。</summary>
public class DecompileCacheTests : IDisposable
{
    public DecompileCacheTests() { }
    public void Dispose()
    {
        DecompileCache.Clear();
        FastPushContext.ResetForTest();
    }

    [Fact]
    public void StoreThenTryGet_SameEntryWay_Hit()
    {
        DecompileCache.Store("gml_GlobalScript_table_weapons", PatchingWay.GML, "return [\"a\"];");
        Assert.True(DecompileCache.TryGet("gml_GlobalScript_table_weapons", PatchingWay.GML, out string? text));
        Assert.Equal("return [\"a\"];", text);
    }

    [Fact]
    public void TryGet_MissingOrOtherWay_Miss()
    {
        DecompileCache.Store("gml_Script_scr_x", PatchingWay.GML, "return 1;");
        Assert.False(DecompileCache.TryGet("gml_Script_scr_x", PatchingWay.AssemblyAsString, out _));
        Assert.False(DecompileCache.TryGet("gml_Script_never", PatchingWay.GML, out _));
    }

    [Fact]
    public void TryGet_Overwrite_LatestWins()
    {
        DecompileCache.Store("e1", PatchingWay.GML, "old");
        DecompileCache.Store("e1", PatchingWay.GML, "new");
        Assert.True(DecompileCache.TryGet("e1", PatchingWay.GML, out string? t));
        Assert.Equal("new", t);
    }
}
