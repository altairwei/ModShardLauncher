using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 4] 结构 op 快推门控：同名同体 → 幂等重放（账本命中全 skip）；
/// 同名异体 → 摘旧重建（图里最终只有一份该名函数、体为最后一次调用）。</summary>
[Collection("vanilla")]
public class AddCodeFastGateTests : IDisposable
{
    readonly UndertaleData savedData;

    public AddCodeFastGateTests()
    {
        savedData = DataLoader.data;
        FastPushContext.ResetForTest();
    }

    public void Dispose()
    {
        DataLoader.data = savedData;
        FastPushContext.ResetForTest();
        FinalTextStore.ResetRound();
        CompileLedger.Clear();
    }

    static UndertaleData Load()
    {
        using var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var d = UndertaleIO.Read(fs, w => { });
        DataLoader.data = d;
        return d;
    }

    [Fact]
    public void AddFunction_FastMode_SameBodyTwice_SingleEntry()
    {
        var g = Load();
        FastPushContext.BeginPush();
        Msl.AddFunction("function scr_fg1() { return 1; }", "scr_fg1");
        var first = g.Code.Count(c => c.Name.Content == "scr_fg1");
        Msl.AddFunction("function scr_fg1() { return 1; }", "scr_fg1");   // 同体重放
        var second = g.Code.Count(c => c.Name.Content == "scr_fg1");
        FastPushContext.EndPush();
        Assert.Equal(1, first);
        Assert.Equal(1, second);   // 冷账本时第二次走「删旧重建」，图仍一份；账本已记后走 skip——两种路径都不重复
        Assert.True(CompileLedger.IsCurrent("scr_fg1",
            TextHash.Hash("function scr_fg1() { return 1; }")));   // 尾账已记（重放轮判据）
    }

    [Fact]
    public void AddFunction_FastMode_DifferentBody_RebuildsSingleEntry()
    {
        var g = Load();
        FastPushContext.BeginPush();
        Msl.AddFunction("function scr_fg2() { return 1; }", "scr_fg2");
        Msl.AddFunction("function scr_fg2() { return 2; }", "scr_fg2");   // 同名异体 → 删旧重建
        FastPushContext.EndPush();
        Assert.Equal(1, g.Code.Count(c => c.Name.Content == "scr_fg2"));   // 图里只有一份根条目
        // 子 wrapper（gml_Script_*，编译器建、ParentEntry 已就地设置）被一并摘除，无孤儿副本
        Assert.True(g.Code.Count(c => c.Name?.Content?.StartsWith("gml_Script_scr_fg2") == true) <= 1);
        // Script/Function 以子条目名命名——对称摘除后恰一份（漏摘会留下指向已删 Code 的死引用，
        // 重编译配对预扫描 NRE——回归锚点）
        Assert.Equal(1, g.Scripts.Count(s => s.Name?.Content == "gml_Script_scr_fg2"));
        Assert.Equal(1, g.Functions.Count(f => f.Name?.Content == "gml_Script_scr_fg2"));
        // 终稿 last-writer-wins = 第二次体；账本哈希同口径
        Assert.True(FinalTextStore.TryGet("scr_fg2", out var text, out _));
        Assert.Equal("function scr_fg2() { return 2; }", text);
        Assert.True(CompileLedger.IsCurrent("scr_fg2", TextHash.Hash("function scr_fg2() { return 2; }")));
    }
}
