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
/// 重试期间不 Fail（瞬态），只有耗尽放弃才 Fail。
/// fix-loop #24 追加：默认轮 probe 门控扫描 + 稳定性成功门（达标须等计数确认）+
/// 平台期恰一次全扫兜底——22min → 预期秒级/轮。</summary>
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
        Mem.TestRegions = null;
        Mem.LastProbe = default;
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
        NodeIndex.RetryIntervalMs = 100;              // #24 稳定性门：成功须第二轮确认等计数——
                                                      // 5s 默认间隔会把确认轮推出 WaitReady 窗口
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

    /// <summary>fix-loop #15（13:40 proof 31/4 根因）：GMS 2.3 匿名函数名全库 403 条 >128 字符
    /// （最长 513）。NodeIndex 读名 ReadCString(…,128) 把 node 名截成 128 前缀当 key →
    /// TryGet(全名) 永远 miss → Translator.ResolveCall 误报「not in registry, not in node
    /// index」（node 其实都在）。名字取 agent.log:6637 四条失败名里最短的一条（132 字符）钉住
    /// 真实形态。</summary>
    [Fact]
    public void Build_LongEntryName_IsIndexedUnderFullName()
    {
        string name = "gml_Script____struct___1_anon_ctr_fog_raycast_gml_GlobalScript_ctr_fog_raycast_1825_ctr_fog_raycast_gml_GlobalScript_ctr_fog_raycast";
        PlantNode(0x10100, 0x10C00, name);
        Assert.Equal(1, NodeIndex.Build());
        Assert.True(NodeIndex.TryGet(name, out _));  // 全名（非 128 前缀）必须可查
    }

    // ---- fix-loop #24：probe 门控扫描（22min 索引的真根因不是扫描带宽，是全量扫描把
    // 游戏 3.3GB 工作集逐出 → 之后 38K 命中 × ~7 次散读全吃 ~4ms 硬页错误。#11 离线
    // dump：命中节点聚 19 区段 ~20MB、每 16KB 页 ~16 节点均匀密度 → 区段头 16KB 探针
    // 必命中。门控把扫描量 3.5GB → ~20MB，工作集不动 → 秒级）。----

    [Fact]
    public void ScanQwordProbed_OnlyScansRegionsWhoseProbeHits()
    {
        Mem.TestMap = new byte[0x8000];
        Mem.TestRegions = new List<(ulong, ulong)> { (Base, 0x4000), (Base + 0x4000, 0x4000) };
        W64(Base + 0x4000, SIG);                     // SIG 只在第二区段头
        W64(Base + 0x4010, 0xDEADBEEFUL);            // 同段第二 qword（非 SIG）——整段扫也只 1 命中
        var hits = Mem.ScanQwordProbed(SIG);
        var hit = Assert.Single(hits);
        Assert.Equal(Base + 0x4000, hit);            // 区段一（探针无 SIG）整段跳过；命中全在区段二
        Assert.Equal((2, 1), Mem.LastProbe);         // 日志证据源：探测 2 段、命中 1 段
    }

    /// <summary>探针窗语义钉版：SIG 深埋区段头 16KB 之外 → 门控扫不到（by design——
    /// 均匀密度下不该发生；万一发生由 NodeIndex 平台期全扫兜底，见下一用例）。</summary>
    [Fact]
    public void ScanQwordProbed_SkipsSigBeyondProbeWindow_FullScanFindsIt()
    {
        Mem.TestMap = new byte[0x8000];              // 32KB：SIG 深埋 0x5000（>16KB 探针窗；
                                                    // < 64KB 网格间距 → v2 网格也不覆盖，见下）
        W64(Base + 0x5000, SIG);
        Assert.Empty(Mem.ScanQwordProbed(SIG));      // 头 16KB 窗无 SIG → 整段跳过
        var hit = Assert.Single(Mem.ScanQword(SIG)); // 全扫（兜底路径的原语）找得到
        Assert.Equal(Base + 0x5000, hit);
    }

    // ---- fix-loop #24 v2：尾溢区段。13:54 boot 真机形态（外扫钉死）：17 个节点专属
    // 区段 SIG 在头 0x100–0x400（头窗全中），但专属区段装满后尾批节点（全是
    // gml_RoomCC_*_Create，766 原始/378 去重名）溢进两个共享堆区段深处——首个 SIG 在
    // +0x53E00 / +0xE8A00，头 16KB 探针必漏。v2 = 网格窗：每 64KB 一个 1KB 窗，
    // 保证节点 run（≥64KB+窗宽）必含一个窗（任意 ≥64KB 区间必含 64K 整倍数点；
    // 1KB 窗宽于节点间距 ~176–257B → 必含一个节点头，与对齐无关）。----

    /// <summary>生产形态复刻（13:54 boot 外扫的逐字段数字）：两个 1028K 区段、
    /// 392+374 个 256B 间距节点 run 深埋 +0x53E00/+0xE8A00 → v2 网格必须全收。</summary>
    [Fact]
    public void ScanQwordProbed_GridCatchesTailSpillRuns_ProductionShape()
    {
        Mem.TestMap = new byte[0x202000];
        Mem.TestRegions = new List<(ulong, ulong)> { (Base, 0x101000), (Base + 0x101000, 0x101000) };
        void Spill(ulong regionBase, uint firstOff, int count)   // 节点 run：256B 间距 × count
        {
            for (int i = 0; i < count; i++) W64(regionBase + firstOff + (ulong)i * 0x100, SIG);
        }
        Spill(Base, 0x53E00, 392);
        Spill(Base + 0x101000, 0xE8A00, 374);
        var hits = Mem.ScanQwordProbed(SIG);
        Assert.Equal(392 + 374, hits.Count);         // 两个尾溢 run 全部收入
        Assert.Contains(Base + 0x53E00, hits);
        Assert.Contains(Base + 0x101000 + 0xE8A00, hits);
        Assert.Equal((2, 2), Mem.LastProbe);         // 两段都被探针判「含节点」
    }

    /// <summary>网格窗语义钉版（成本边界）：窗只开在 64K 整倍数处、宽 1KB——孤立 SIG
    /// 落在窗外仍跳过。网格保证的是「run ≥ 64KB 必中」，不是任意 SIG 必中。</summary>
    [Fact]
    public void ScanQwordProbed_GridWindowsAre1KbAt64KSpacing_SingleSigBetweenWindowsStillSkipped()
    {
        Mem.TestMap = new byte[0x30000];             // 192KB：网格点 0x10000/0x20000
        W64(Base + 0x10800, SIG);                    // 网点 +0x800：窗外（>1KB）
        Assert.Empty(Mem.ScanQwordProbed(SIG));
        Mem.TestMap = new byte[0x30000];
        W64(Base + 0x10008, SIG);                    // 网点 +8：窗内
        var hit = Assert.Single(Mem.ScanQwordProbed(SIG));
        Assert.Equal(Base + 0x10008, hit);
    }

    [Fact]
    public void Build_GatedScan_FindsNodesInProbeHitRegions()
    {
        Mem.TestMap = new byte[0x8000];
        Mem.TestRegions = new List<(ulong, ulong)> { (Base, 0x4000), (Base + 0x4000, 0x4000) };
        PlantNode(Base + 0x4000 + 0x40, Base + 0x6000, "second_region_entry");
        Assert.Equal(1, NodeIndex.Build());          // Build 走门控：区段二探针命中 → 扫到节点
        Assert.True(NodeIndex.TryGet("second_region_entry", out _));
    }

    /// <summary>平台期升级兜底：门控连续两轮**非零**等计数且未达标 → 恰一次全扫回退（老 22min
    /// 行为作正确性保底，不循环烧）。真机对应「门控漏区」场景（计数卡在门控可见子集上）。
    /// fix-loop #26 形态更新：升级前置非零（0,0,0 不再升级，见下一用例）——触发形态改为
    /// 「门控可见 1 个 + 深埋 1 个、MinNodes=2」：门控稳定报 1（子集）→ 平台期 → 全扫收 2。</summary>
    [Fact]
    public void BeginBuild_GatedPlateau_EscalatesToSingleFullScan()
    {
        Mem.TestMap = new byte[0x8000];
        Mem.TestRegions = new List<(ulong, ulong)> { (Base, 0x2000), (Base + 0x2000, 0x6000) };
        PlantNode(0x10100, 0x10C00, "gated_entry");   // 区段一（头窗全覆盖）：门控可见
        PlantNode(0x17000, 0x10D00, "deep_entry");    // 区段二 +0x5000：头窗外、无网格点
        NodeIndex.MinNodes = 2;
        NodeIndex.RetryMaxAttempts = 8;
        NodeIndex.RetryIntervalMs = 1;
        NodeIndex.BeginBuild();                      // 轮 1-3 门控稳定 1 节点（<2）→ 平台期计数到 2 → 轮 4 全扫
        Assert.True(NodeIndex.WaitReady(5000));
        Assert.True(NodeIndex.TryGet("gated_entry", out _));
        Assert.True(NodeIndex.TryGet("deep_entry", out _));   // 全扫兜底找到了深埋节点
        Assert.Equal(1, NodeIndex.FullScans);                 // 恰一次全扫（22min/轮，绝不重复）
        Assert.Equal("ok", AgentState.Status);
    }

    /// <summary>#26（15:52 boot 真机钉版）：注册器后绑定未开始的 0,0,0 轮也是「等计数」——
    /// 但那是「绑定未开始」不是「装载已静默」。零平台期升级全扫会在绑定中途快照：
    /// 真机 attempt 1-3 全 0（regions 0/88→0/344→0/383）→ attempt 4 全扫收 34720/34724
    /// 早收工缺 4 名。0 计数只重试、由重试上限兜底，绝不升级全扫。</summary>
    [Fact]
    public void BeginBuild_ZeroCountRounds_NeverEscalateToFullScan()
    {
        Mem.TestMap = new byte[0x8000];              // 唯一节点深埋 0x5000：门控不可见
        PlantNode(0x15000, 0x16000, "deep_entry");
        NodeIndex.MinNodes = 1;
        NodeIndex.RetryMaxAttempts = 6;
        NodeIndex.RetryIntervalMs = 1;
        NodeIndex.BeginBuild();                      // 全程 0 节点轮 → 放弃（不升级全扫）
        Assert.True(NodeIndex.WaitReady(5000));
        Assert.Equal(0, NodeIndex.FullScans);                 // 今天 boot 的形态：零轮永不触发全扫
        Assert.False(NodeIndex.TryGet("deep_entry", out _));  // 门控看不见，诚实放弃
        Assert.Contains("node index too small (0)", AgentState.Status);
    }

    /// <summary>#24 稳定性成功门：门控快扫后「达标」不再隐含绑定装载完成（老代码靠每轮
    /// 20min 扫描天然等到装载完）。计数爬坡到门槛的那一轮不能收工——还须一轮等计数
    /// 确认装载静默，否则 31K 抢跑收工会缺尾批绑定（#11 真机：31K→34724 仍在涨）。</summary>
    [Fact]
    public void BeginBuild_WaitsForStableCount_BeforeSuccess()
    {
        int calls = 0, grown = 0;
        NodeIndex.BuildDelayHook = () =>
        {
            calls++;
            if (grown >= 3) return;                  // 第 3 个节点后装载「静默」
            grown++;
            PlantNode(0x10100 + (ulong)grown * 0x100, 0x10C00 + (ulong)grown * 0x40, $"entry_{grown}");
        };
        NodeIndex.MinNodes = 3;
        NodeIndex.RetryMaxAttempts = 30;
        NodeIndex.RetryIntervalMs = 10;
        NodeIndex.BeginBuild();                      // 计数序列 1,2,3,3——第 3 轮达标但不等前值，第 4 轮才确认
        Assert.True(NodeIndex.WaitReady(10000));
        Assert.Equal(4, calls);                      // 成功必须多等一轮（老代码 3 轮即收工 = 本用例红）
        Assert.Equal(3, NodeIndex.Count);
        Assert.Equal("ok", AgentState.Status);
    }

    /// <summary>#24 ReadCString 重写（逐字节 VirtualQuery 查界 → 一次查界连续读）的语义
    /// 守卫：跨出可读区段尾即停——与旧实现「首不可读字节停」同界。名字故意不写 NUL，
    /// 钉住「取到界为止、不越界」。</summary>
    [Fact]
    public void ReadCString_ClampsAtRegionEnd_WhenNoNulBeforeIt()
    {
        string tail = "tail_of_region";
        Encoding.ASCII.GetBytes(tail).CopyTo(Mem.TestMap!, (int)(0x2000 - tail.Length));
        Assert.Equal(tail, Mem.ReadCString(Base + 0x2000 - (ulong)tail.Length, 1024));
    }
}
