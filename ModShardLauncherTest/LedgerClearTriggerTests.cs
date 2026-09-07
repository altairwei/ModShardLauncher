using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 3] 双清零咽喉（spec §5）：① LoadUmt（工作图替换，DataLoaded 事件）；
/// ② LockBaseline 命中新哈希（BaselineLocked 事件）。触发②的同哈希重锁不清（§7.7 漂移重放）。</summary>
[Collection("vanilla")]
public class LedgerClearTriggerTests : IDisposable
{
    readonly string? savedHash;

    public LedgerClearTriggerTests()
    {
        // 前置条件隔离：全套回归中 HotPipeline/LiveSession 类会经真实管道触发 LockBaseline，
        // 把 lastPinnedHash 留在静态里（那些类早于 FastPushContext，Dispose 不复位它）——
        // 「首次 pin」断言必须自建干净起点，不依赖跨类卫生。
        FastPushContext.ResetForTest();
        savedHash = DataLoader.VanillaHash;
    }

    public void Dispose()
    {
        DataLoader.VanillaHash = savedHash;   // 测试直接设回（internal set）
        CompileLedger.Clear();
        FastPushContext.ResetForTest();
    }

    [Fact]
    public void LoadUmt_ClearsLedgerAndResetsLag()
    {
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, TextHash.Hash("x"));
        FastPushContext.LagCount = 5;
        DataLoader.LoadUmt(TestData.VanillaPath);
        Assert.Equal(0, CompileLedger.Count);
        Assert.Equal(0, FastPushContext.LagCount);
        // 真 vanilla 名集快照（oBoot 是 seed fixture 的对象，真游戏文件里没有——用真实表名）
        Assert.True(FastPushContext.IsVanillaName("gml_GlobalScript_table_weapons"));
    }

    [Fact]
    public void SameFileReload_KeepsCache()
    {
        // 自足形态：先装载建立当前哈希（这轮清缓存无所谓），再存条目，再同文件重载——
        // 哈希没变 → 缓存保留（顺序无关，不依赖其它测试是否先装载过）
        DataLoader.LoadUmt(TestData.VanillaPath);
        DecompileCache.Store("scr_a", PatchingWay.GML, "t");
        DataLoader.LoadUmt(TestData.VanillaPath);
        Assert.True(DecompileCache.TryGet("scr_a", PatchingWay.GML, out _));
    }

    [Fact]
    public void BaselineLocked_DifferentHash_ClearsLedger()
    {
        // static ctor 已订阅（先读 LagCount 显式保证 ctor 已跑）。直接驱动事件
        // （等价于 LockBaseline 命中新哈希——真实路径用例在 BaselineStoreTests 补）。
        _ = FastPushContext.LagCount;
        string h1 = new('A', 64);
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, TextHash.Hash("x"));
        BaselineStore.RaiseBaselineLockedForTest(h1);
        Assert.Equal(1, CompileLedger.Count);   // 首次 pin：只记不追（上一 pin=null）

        BaselineStore.RaiseBaselineLockedForTest(h1);   // 同哈希重锁：不清（§7.7）
        Assert.Equal(1, CompileLedger.Count);

        string h2 = new('B', 64);
        BaselineStore.RaiseBaselineLockedForTest(h2);   // 异哈希：清
        Assert.Equal(0, CompileLedger.Count);
    }
}
