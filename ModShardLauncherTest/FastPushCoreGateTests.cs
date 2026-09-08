using ModShardLauncher.HotReload;
using UndertaleModLib;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 6] RunPush 前置门（headless 可达部分）：门不过 = Rejected + 指引文案，
/// 不动图不编译。RunPush 全链（LoadFiles→PatchFile→编译→推送）需 ModInfos.Instance——
/// 单测不可达（诚实边界），走 Task 9 E2E（CompileAndPushWith 缝）与冒烟。
/// 操纵的全局静态（Settings/savedDataPath/data/Session/Baseline）与 vanilla 集合同池——
/// 进 [Collection("vanilla")] 串行。</summary>
[Collection("vanilla")]
public class FastPushCoreGateTests : IDisposable
{
    readonly bool savedDevMode = Main.Settings.DevMode;
    readonly string savedSavedDataPath = DataLoader.savedDataPath;
    readonly UndertaleData? savedData = DataLoader.data;

    public FastPushCoreGateTests()
    {
        LiveSession.Current?.End();
        LiveSession.ForRunningGameOverride = null;
        BaselineStore.Reset();
        FastPushContext.ResetForTest();
    }

    public void Dispose()
    {
        Main.Settings.DevMode = savedDevMode;
        DataLoader.savedDataPath = savedSavedDataPath;
        if (savedData != null) DataLoader.data = savedData;
        LiveSession.Current?.End();
        LiveSession.ForRunningGameOverride = null;
        BaselineStore.Reset();
        FastPushContext.ResetForTest();
        CompileLedger.Clear();
        FinalTextStore.ResetRound();
    }

    [Fact]
    public void RunPush_NoSavedData_RejectedWithGuidance()
    {
        Main.Settings.DevMode = true;
        DataLoader.savedDataPath = "";
        var r = FastPushCore.RunPush();
        Assert.True(r.Rejected);
        Assert.False(r.Attempted);
        Assert.Contains("完整编译", r.RejectionReason);
    }

    [Fact]
    public void RunPush_DevOff_Rejected()
    {
        Main.Settings.DevMode = false;
        DataLoader.savedDataPath = "C:/x/data.win";
        var r = FastPushCore.RunPush();
        Assert.True(r.Rejected);
        Assert.False(r.Attempted);
    }

    [Fact]
    public void RunPush_NoDataWin_Rejected()
    {
        Main.Settings.DevMode = true;
        DataLoader.savedDataPath = "C:/x/data.win";
        DataLoader.data = new UndertaleData();   // FORM == null：未开档
        var r = FastPushCore.RunPush();
        Assert.True(r.Rejected);
        Assert.False(r.Attempted);
        Assert.Contains("data.win", r.RejectionReason);
    }

    [Fact]
    public void RunPush_DeadSession_Rejected()
    {
        Main.Settings.DevMode = true;
        DataLoader.savedDataPath = "C:/x/data.win";
        DataLoader.data = new UndertaleData { FORM = new UndertaleChunkFORM() };   // 门 3 过
        // 死会话形态（#25 语义）：会话对象在但已 End → Current 为空 → 拒（不做自动重连）
        var s = new LiveSession("msl-gate-test", () => "v", () => "2022.9",
            () => new List<(string, string)>(), new LiveQuotas(), () => new List<(int, string)>());
        s.TargetPid = 999999;   // internal 经 IVT：绑定不存在的 PID
        s.End();
        var r = FastPushCore.RunPush();
        Assert.True(r.Rejected);
        Assert.False(r.Attempted);
        Assert.Contains("活会话", r.RejectionReason);
    }

    [Fact]
    public void NeedsPristineReload_TrueWhenLagOrLedger()
    {
        FastPushContext.ResetForTest();
        CompileLedger.Clear();
        Assert.False(FastPushCore.NeedsPristineReload);   // 干净会话：不重载（零成本）
        FastPushContext.LagCount = 3;
        Assert.True(FastPushCore.NeedsPristineReload);    // 滞后>0：图带漂移
        FastPushContext.LagCount = 0;
        CompileLedger.MarkCompiled("scr_a", PatchingWay.GML, TextHash.Hash("x"));
        Assert.True(FastPushCore.NeedsPristineReload);    // 账本非空：图被快推碰过
    }
}
