using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 4] fast 模式 Save = 记账不编译：终稿入 FinalTextStore、图零字节漂移。
/// 计划缺陷修正 #7：用真实表名（oBoot 是 seed fixture 的）。断言强化：原稿的 IsDirty 断言
/// 排在 EndPush（清脏集）之后属空真——改为清集前取值，并加双反编译字节恒等断言。</summary>
[Collection("vanilla")]
public class SaveRedirectTests : IDisposable
{
    const string EntryName = "gml_GlobalScript_table_weapons";

    readonly UndertaleData savedData;

    public SaveRedirectTests()
    {
        savedData = DataLoader.data;
        FastPushContext.ResetForTest();
    }

    public void Dispose()
    {
        DataLoader.data = savedData;
        FastPushContext.ResetForTest();
        FinalTextStore.ResetRound();
    }

    static UndertaleData Load()
    {
        using var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var d = UndertaleIO.Read(fs, w => { });
        DataLoader.data = d;
        return d;
    }

    [Fact]
    public void Save_FastMode_RecordsWithoutCompiling()
    {
        var g = Load();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        string vanillaText = UndertaleModLib.Decompiler.Decompiler.Decompile(entry,
            new UndertaleModLib.Decompiler.GlobalDecompileContext(g, false));

        var fe = new FileEnumerable<string>(new Header(EntryName, entry, PatchingWay.GML),
            new[] { "return 55;" });
        FastPushContext.BeginPush();
        var summary = fe.Save();
        bool dirtyBeforeRoundEnd = FastPushContext.IsDirty(EntryName);   // 在 EndPush 清脏集前取值
        FastPushContext.EndPush();

        Assert.Equal("return 55;", summary.newCode);
        Assert.True(FinalTextStore.TryGet(EntryName, out var text, out _));
        Assert.Equal("return 55;", text);
        // 图未变：fast Save 只记账不动图 → 无脏标记（非空真版）
        Assert.False(dirtyBeforeRoundEnd);
        // 图未变（硬判据）：指令流零漂移 → 两次直接反编译逐字相等
        string afterText = UndertaleModLib.Decompiler.Decompiler.Decompile(entry,
            new UndertaleModLib.Decompiler.GlobalDecompileContext(g, false));
        Assert.Equal(vanillaText, afterText);
    }
}
