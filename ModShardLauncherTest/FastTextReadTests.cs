using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 4] FastText.Read 读序：FinalTextStore（仅 fast）→ DecompileCache（双模式）
/// → 反编译工作图（可缓存三门槛：vanilla 名 ∧ 账本无 ∧ 非脏）。
/// 计划缺陷修正 #7：oBoot 是 seed fixture 对象、真游戏文件没有——全部改用真实表名
/// gml_GlobalScript_table_weapons；#9：终稿站是 fast 专属，首站命中测试须自建 BeginPush
/// （否则与 Read_FullMode_IgnoresStaleFinalTextStore 的语义互斥）。</summary>
[Collection("vanilla")]
public class FastTextReadTests : IDisposable
{
    const string EntryName = "gml_GlobalScript_table_weapons";

    readonly UndertaleData savedData;

    public FastTextReadTests()
    {
        savedData = DataLoader.data;
        FastPushContext.ResetForTest();   // 触发 static ctor 订阅 + 清状态
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
    public void Read_FinalTextStoreHit_DoesNotTouchGraph()
    {
        var g = Load();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        FastPushContext.BeginPush();   // 终稿站仅 fast 模式查（#9：full 模式必须无视残稿）
        FinalTextStore.Record(EntryName, PatchingWay.GML, "function fake() {}");
        var text = FastText.Read(entry, EntryName, PatchingWay.GML);
        FastPushContext.EndPush();
        Assert.Equal("function fake() {}", text);   // 链上家优先
    }

    [Fact]
    public void Read_FullMode_IgnoresStaleFinalTextStore()
    {
        var g = Load();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        // 非 InFastPush（上一快推轮的残稿还在终稿存储里）→ full 模式读者不得吃到它
        FinalTextStore.Record(EntryName, PatchingWay.GML, "function stale() {}");
        var text = FastText.Read(entry, EntryName, PatchingWay.GML);
        string direct = UndertaleModLib.Decompiler.Decompiler.Decompile(entry,
            new UndertaleModLib.Decompiler.GlobalDecompileContext(g, false));
        Assert.Equal(direct, text);   // full 模式 = 反编译当前图（真相源语义，spec §7.2）
    }

    [Fact]
    public void Read_CacheHit_ServesStoredText()
    {
        var g = Load();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        // 冷路径先喂缓存（模拟早前装载期已热）——缓存站双模式共用
        DecompileCache.Store(EntryName, PatchingWay.GML, "return 123;");
        var text = FastText.Read(entry, EntryName, PatchingWay.GML);
        Assert.Equal("return 123;", text);
    }

    [Fact]
    public void Read_ColdMiss_DecompilesGraphAndStores()
    {
        var g = Load();
        DataLoader.VanillaHash = "ABC";
        FastPushContext.OnDataReloaded();   // 快照 vanilla 名集
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        var text = FastText.Read(entry, EntryName, PatchingWay.GML);
        Assert.False(string.IsNullOrEmpty(text));   // 反编译产物非空
        // 与直接反编译同源（读序冷路径 == 原 Decompile 行为——full 模式行为不变性）
        string direct = UndertaleModLib.Decompiler.Decompiler.Decompile(entry,
            new UndertaleModLib.Decompiler.GlobalDecompileContext(g, false));
        Assert.Equal(direct, text);
        Assert.True(DecompileCache.TryGet(EntryName, PatchingWay.GML, out _));   // 可缓存名 → 已存
    }

    [Fact]
    public void Read_NotVanillaName_DecompilesWithoutStoring()
    {
        var g = Load();
        DataLoader.VanillaHash = "ABC";
        FastPushContext.OnDataReloaded();   // 快照在 AddCode 之前 → 新名不在快照内
        Msl.AddCode("return 7;", "scr_v2_madeup");   // 生产 API 建非 vanilla 名条目（full 模式语义）
        var code = g.Code.First(c => c.Name.Content == "scr_v2_madeup");
        var text = FastText.Read(code, "scr_v2_madeup", PatchingWay.GML);
        // 计划缺陷 #11：反编译产物带尾随换行，字面全等过强——改为与直接反编译同源全等（更强的语义不变性）
        string direct = UndertaleModLib.Decompiler.Decompiler.Decompile(code,
            new UndertaleModLib.Decompiler.GlobalDecompileContext(g, false));
        Assert.Equal(direct, text);
        Assert.Contains("return 7;", text);   // 体确实是我们 AddCode 进去的体
        Assert.False(DecompileCache.TryGet("scr_v2_madeup", PatchingWay.GML, out _));
    }

    [Fact]
    public void Read_MidChainDirty_DoesNotStorePoisonedVanilla()
    {
        var g = Load();
        DataLoader.VanillaHash = "ABC";
        FastPushContext.OnDataReloaded();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        // 模拟 full 补丁链内：前一个 mod 已改过该条目（Save → NoteMutated）
        FastPushContext.NoteDirty(EntryName);
        var text = FastText.Read(entry, EntryName, PatchingWay.GML);
        Assert.False(string.IsNullOrEmpty(text));
        Assert.False(DecompileCache.TryGet(EntryName, PatchingWay.GML, out _));   // 不存毒
    }

    [Fact]
    public void Read_DirtyEntry_DoesNotStore()
    {
        var g = Load();
        DataLoader.VanillaHash = "ABC";
        FastPushContext.OnDataReloaded();
        var entry = g.Code.First(c => c.Name.Content == EntryName);
        FastPushContext.BeginPush();
        FastPushContext.NoteDirty(EntryName);   // 本轮已被改
        FastText.Read(entry, EntryName, PatchingWay.GML);
        Assert.False(DecompileCache.TryGet(EntryName, PatchingWay.GML, out _));
        FastPushContext.EndPush();
    }
}
