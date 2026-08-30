using System.Diagnostics;
using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>fix-loop #9：节点索引构建移出握手路径。真机实测（2026-08-30 18:00 会话）：
/// 首连 SelfCheck 里同步 NodeIndex.Build 跑了 26min43s（38,069 命中 × 散读，
/// 游戏 3.3GB 工作集下每次解引用 ~4ms 页错误），hello 迟到 MSL 10s 超时之后——
/// 15:01/15:37/18:00 三次「receive timeout」全是这一处。
/// 新契约：BeginBuild 在 boot 期（OnInitGML）后台构建；hello 每连接快照索引现状。
/// fix-loop #10 追加：OnInitGML 注册器时刻（+0.5s）执行节点尚未创建（真机两次实测
/// 0 nodes/75ms）——节点是 data.win 装载绑定（注册器之后）才批量出现。构建线程
/// 带有界重试（MinNodes 门槛 + RetryMaxAttempts/RetryIntervalMs，seam 可调），
/// 重试期间不 Fail（瞬态），只有耗尽放弃才 Fail。</summary>
public class IndexBuildTests : IDisposable
{
    const ulong SIG = 0x1406BE508;      // 与 NodeValidationTests 同形（扫描靶值）
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    public IndexBuildTests()
    {
        AgentState.ResetForTest();
        Mem.TestMap = new byte[0x2000];
        Mem.TestBase = Base;
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
    }

    public void Dispose()
    {
        NodeIndex.BuildDelayHook = null;
        NodeIndex.ResetForTest();
        Mem.TestMap = null;
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));

    static void PlantNode(ulong node, ulong nameAddr, string name)
    {
        W64(node, SIG);
        W32(node + 0x64, 0x00FFFFFF);
        W64(node + 0x68, 0x10800);
        W64(0x10800, EXEC);
        W64(node + 0x80, nameAddr);
        Encoding.ASCII.GetBytes(name).CopyTo(Mem.TestMap!, (int)(nameAddr - Base));
    }

    [Fact]
    public void BeginBuild_RunsInBackground_AndSwapsSnapshotIn()
    {
        PlantNode(0x10100, 0x10C00, "test_entry");
        NodeIndex.MinNodes = 1;                       // 本用例只测后台 + 快照换入，不测规模护栏
        NodeIndex.BuildDelayHook = () => Thread.Sleep(400);
        Assert.False(NodeIndex.Ready);
        NodeIndex.BeginBuild();
        Assert.False(NodeIndex.Ready);              // 400ms 缝未走完：构建确实在后台线程
        Assert.True(NodeIndex.WaitReady(5000));     // 有界等待等到完成
        Assert.True(NodeIndex.TryGet("test_entry", out _));   // 快照已换入可查
    }

    [Fact]
    public void BeginBuild_SmallIndex_GivesUpAndFails()
    {
        PlantNode(0x10100, 0x10C00, "test_entry");   // 1 节点 < 30000 → 重试耗尽后放弃并 Fail
        NodeIndex.RetryMaxAttempts = 1;
        NodeIndex.RetryIntervalMs = 1;
        NodeIndex.BeginBuild();
        Assert.True(NodeIndex.WaitReady(5000));
        Assert.Contains("node index too small", AgentState.Status);
    }

    [Fact]
    public void BeginBuild_RetriesUntilNodesAppear()
    {
        // fix-loop #10 真机证据：OnInitGML 注册器时刻（+0.5s）节点尚未创建（0 nodes/75ms 两次
        // 实测）——节点是 data.win 装载绑定（注册器之后）才批量出现的。构建线程必须重试等它
        NodeIndex.MinNodes = 1;
        NodeIndex.RetryMaxAttempts = 20;
        NodeIndex.RetryIntervalMs = 200;
        NodeIndex.BeginBuild();                      // 首扫：TestMap 空 → 0 节点 → 进重试
        Thread.Sleep(100);
        PlantNode(0x10100, 0x10C00, "late_entry");   // 节点「随后出现」（绑定完成）
        Assert.True(NodeIndex.WaitReady(10000));
        Assert.True(NodeIndex.TryGet("late_entry", out _));
        Assert.Equal("ok", AgentState.Status);       // 重试期间不 Fail——最终成功则状态干净
    }

    [Fact]
    public void BeginBuild_GivesUpAfterMaxAttempts()
    {
        NodeIndex.MinNodes = 1;                      // 空表 0 节点，永远不达门槛
        NodeIndex.RetryMaxAttempts = 2;
        NodeIndex.RetryIntervalMs = 10;
        NodeIndex.BeginBuild();
        Assert.True(NodeIndex.WaitReady(5000));
        Assert.True(NodeIndex.Ready);                // 放弃也算收尾（hello 如实带 Fail 状态）
        Assert.Contains("node index too small (0)", AgentState.Status);
    }

    [Fact]
    public void WaitReady_NeverStarted_ReturnsImmediately()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(NodeIndex.WaitReady(8000));     // 未启动不等待（异常 boot/测试进程的常态）
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void Build_IsRepeatable_SnapshotSwappedNotMerged()
    {
        PlantNode(0x10100, 0x10C00, "a");
        Assert.Equal(1, NodeIndex.Build());
        Assert.True(NodeIndex.TryGet("a", out _));
        Mem.TestMap = new byte[0x2000];              // 全新内存：旧快照应被整体替换而非残留
        PlantNode(0x10100, 0x10D00, "b");
        Assert.Equal(1, NodeIndex.Build());
        Assert.False(NodeIndex.TryGet("a", out _));
        Assert.True(NodeIndex.TryGet("b", out _));
    }
}
