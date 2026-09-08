using ModShardLauncher.HotReload;
using UndertaleModLib;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 6] Execute 真图测试：计划 → 编译进工作图 → 账本记账。不依赖集合共享装载态
///（Execute 真改图——每测 fresh read，Dispose 复原）。条目选 gml_GlobalScript_table_weapons
//（vanilla 表脚本，ReplaceGML 安全面与 MSL 惯用测试一致）。</summary>
[Collection("vanilla")]
public class CompilePlannerExecuteTests : IDisposable
{
    readonly UndertaleData? savedData = DataLoader.data;

    public CompilePlannerExecuteTests()
    {
        FastPushContext.ResetForTest();   // CompileCount 归零（装载挂钩不触发——直读不经 LoadUmt）
    }

    public void Dispose()
    {
        if (savedData != null) DataLoader.data = savedData;
        FinalTextStore.ResetRound();
        CompileLedger.Clear();
        DecompileCache.Clear();
        FastPushContext.ResetForTest();
    }

    static UndertaleData Load()
    {
        using var s = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var d = UndertaleIO.Read(s, w => { });
        DataLoader.data = d;
        return d;
    }

    [Fact]
    public void Execute_CompilesAndMarksLedger()
    {
        var g = Load();
        const string name = "gml_GlobalScript_table_weapons";   // 真 vanilla 条目（MSL 惯用表脚本）
        var plan = new FastPushPlan(
            new List<PlannedCompile> { new(name, PatchingWay.GML, "return [\"q\"];") },
            new List<PlannedCompile>(), new List<string>());
        CompilePlanner.Execute(plan);
        Assert.Equal(1, FastPushContext.CompileCount);
        Assert.True(CompileLedger.IsCurrent(name, TextHash.Hash("return [\"q\"];")));

        // 图内真值：编译产物读得回（stub 直编不进终稿存储——这里读的是图本体）
        string reread = FastText.Read(
            g.Code.First(c => c.Name.Content == name), name, PatchingWay.GML);
        Assert.Contains("q", reread);
    }

    [Fact]
    public void Execute_MissingGraphEntry_ThrowsFailClosed()
    {
        // 最小图（空 CODE 列表）——免 vanilla 装载：GraphEntry 对 miss 条目抛 InvalidOperationException
        var g = new UndertaleData { FORM = new UndertaleChunkFORM() };
        g.FORM.Chunks["CODE"] = new UndertaleChunkCODE();
        DataLoader.data = g;
        var plan = new FastPushPlan(
            new List<PlannedCompile> { new("no_such_entry_anywhere", PatchingWay.GML, "return 0;") },
            new List<PlannedCompile>(), new List<string>());
        Assert.Throws<InvalidOperationException>(() => CompilePlanner.Execute(plan));
        Assert.Equal(0, FastPushContext.CompileCount);   // fail-closed：未编任何条目
    }
}
