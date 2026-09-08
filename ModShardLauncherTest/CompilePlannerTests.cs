using ModShardLauncher.HotReload;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 6] 纯计划测试：终稿存储（本轮 .sml 重放产物）∩ 账本（已编进工作图的文本）
/// → 变更集 / 回滚集 / 诚实跳过集。只判不动图——真图编译走 CompilePlannerExecuteTests。</summary>
public class CompilePlannerTests : IDisposable
{
    public void Dispose()
    {
        FinalTextStore.ResetRound();
        CompileLedger.Clear();
        DecompileCache.Clear();
        FastPushContext.ResetForTest();
    }

    [Fact]
    public void Plan_ChangedOnly_CompilesDiffFromLedger()
    {
        string h1 = TextHash.Hash("return 1;");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, h1);   // 账本=1
        FinalTextStore.Record("scr_a", PatchingWay.GML, "return 2;");   // 本轮=2 → 变
        FinalTextStore.Record("scr_b", PatchingWay.GML, "return 1;");   // 本轮=1 账本无 → 档1 全编
        var plan = CompilePlanner.Plan();
        Assert.Equal(new[] { "scr_a", "scr_b" }, plan.Compiles.Select(c => c.Entry).OrderBy(x => x).ToArray());
        Assert.Empty(plan.Rollbacks);
    }

    [Fact]
    public void Plan_Unchanged_CompilesNothing()
    {
        string h = TextHash.Hash("return 1;");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, h);
        FinalTextStore.Record("scr_a", PatchingWay.GML, "return 1;");
        var plan = CompilePlanner.Plan();
        Assert.Empty(plan.Compiles);
        Assert.Empty(plan.Rollbacks);
    }

    [Fact]
    public void Plan_VanishedWithVanillaCache_RollsBack()
    {
        string h = TextHash.Hash("return 2;");   // 上次编进去的是 2（mod 版）
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, h);
        DecompileCache.Store("scr_a", PatchingWay.GML, "return 1;");   // vanilla 缓存
        // 本轮无人写 scr_a → 回滚
        var plan = CompilePlanner.Plan();
        var rb = Assert.Single(plan.Rollbacks);
        Assert.Equal("scr_a", rb.Entry);
        Assert.Equal("return 1;", rb.Text);
        Assert.Empty(plan.Compiles);
    }

    [Fact]
    public void Plan_VanishedWithoutVanillaCache_SkippedHonestly()
    {
        string h = TextHash.Hash("x");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.AssemblyAsString, h);   // asm 途径无缓存
        var plan = CompilePlanner.Plan();
        Assert.Empty(plan.Rollbacks);
        Assert.Contains("scr_a", plan.SkippedRollbacks);
    }

    [Fact]
    public void Plan_VanishedAsmWay_SkippedEvenWithCache()
    {
        // Task 6 裁决：asm 途径回滚跳过——即使缓存里有 vanilla asm 也不回
        //（disassemble→assemble 往返不保证逐字节还原，回滚宁缺毋滥）
        string h = TextHash.Hash("push.i 42");
        CompileLedger.MarkCompiled("scr_a", PatchingWay.AssemblyAsString, h);
        DecompileCache.Store("scr_a", PatchingWay.AssemblyAsString, "push.i 7");
        var plan = CompilePlanner.Plan();
        Assert.Empty(plan.Rollbacks);
        Assert.Contains("scr_a", plan.SkippedRollbacks);
    }

    [Fact]
    public void Plan_RolledBackOnce_DoesNotRollBackAgain()
    {
        // 回滚编译后账本记 vanilla 哈希 → 下一轮同判据不得再排回滚（防每轮白编）
        string vanilla = "return 1;";
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, TextHash.Hash(vanilla));
        DecompileCache.Store("scr_a", PatchingWay.GML, vanilla);
        var plan = CompilePlanner.Plan();
        Assert.Empty(plan.Rollbacks);
        Assert.Empty(plan.SkippedRollbacks);
        Assert.Empty(plan.Compiles);
    }
}
