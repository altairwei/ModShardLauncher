namespace MslLive.Agent;

public sealed record NodeInfo(ulong Node, ulong Record, uint CodeId, uint StartOff,
    uint Locals, uint Argc, ulong BufPtr, uint BufLen);

public static class NodeIndex
{
    // 快照换入（fix-loop #9）：构建方写一份全新字典后 Volatile 换引用；读者持旧快照继续用，
    // 零锁并发安全。byName 由此不再是 readonly。
    static Dictionary<string, NodeInfo> byName = new();
    static readonly object startLock = new();
    static volatile bool started;
    static readonly ManualResetEventSlim ready = new(false);

    /// <summary>测试缝：Build 入口注入延迟（钉「hello 不等构建」的时序契约）。</summary>
    internal static Action? BuildDelayHook;

    // fix-loop #10 真机证据（agent.log 21:17/21:29 两次 boot）：OnInitGML 注册器时刻
    // （natives registered 后 ~80ms）执行节点尚未创建——扫描 75ms 即完成、0 命中；节点是
    // data.win 装载绑定（注册器之后的 boot 阶段）才批量出现的（旁证：15:25 主菜单态索引
    // 已有 34,720 ≈ 全部 34,743 个 code entry——装载期创建，非游玩惰性创建）。
    // 因此 boot 期构建带有界重试：等绑定完成再扫；seam 供测试调节奏。
    /// <summary>规模门槛：一次构建收录 ≥ 此数即视为成功（Stoneshard ~34,720）。</summary>
    internal static int MinNodes = 30000;
    /// <summary>最多尝试次数（默认 60 × 5s = 5 分钟窗口，覆盖最慢 boot+装载）。</summary>
    internal static int RetryMaxAttempts = 60;
    /// <summary>尝试间隔（boot 期扫描本身 ~秒级，间隔无须太密）。</summary>
    internal static int RetryIntervalMs = 5000;

    // fix-loop #15（13:40 proof 31/4 根因）：读名截断 128 字符导致长名条目查不到。
    // GMS 2.3 匿名函数名全库 403 条 >128 字符（F049BBB3 实测，最长 513——agent.log:6637
    // 四条失败名 201/132/137/513）。128 前缀当 key → TryGet(全名) 永远 miss →
    // Translator.ResolveCall 对匿名函数引用误报「not in registry, not in node index」
    // （node 其实都在——索引计数缺口由同前缀兄弟名碰撞塌缩独立解释：34422−4=34418
    // 期望 vs 34412 实测，仅剩 ~6 条另有原因）。上限 1024 = 实测最长再留一倍余量；
    // ReadCString 有 NUL 与逐字节可读性双守卫，读长只影响那 403 条，平均名长 ~30 不变。
    internal const int NodeNameMax = 1024;

    /// <summary>boot 期（OnInitGML 注册器之后）后台构建索引。
    /// fix-loop #9：原先在首连 SelfCheck 里同步跑——真机实测 26min43s（扫全部提交私有 RW 区 +
    /// 38K 命中散读，游戏 3.3GB 工作集被扫描逐出后每次解引用 ~4ms 页错误），hello 被堵到
    /// MSL 10s 超时之后才发出。
    /// fix-loop #10：该时刻执行节点尚未创建（见上）——构建带重试直到达 MinNodes 门槛；
    /// 重试期间是瞬态（hello 报 "node index building"，不进 Fail 累积），只有耗尽放弃才
    /// Fail（构建线程是唯一知道总数的地方，护栏在这报）。就算慢，握手路径也没人等它——
    /// hello 每连接快照现状（PipeServer.Handle）。</summary>
    public static void BeginBuild()
    {
        lock (startLock)
        {
            if (started) return;
            started = true;
        }
        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 1; ; attempt++)
            {
                int nodes;
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    nodes = Build();
                }
                catch (Exception ex)
                {
                    AgentState.Fail("node index build failed: " + ex.Message);
                    AgentState.Log("node index build failed: " + ex);
                    break;
                }
                if (nodes >= MinNodes)
                {
                    AgentState.Log($"node index built: {nodes} nodes in {sw.ElapsedMilliseconds}ms (attempt {attempt})");
                    break;
                }
                AgentState.Log($"node index attempt {attempt}: {nodes} nodes in {t.ElapsedMilliseconds}ms, retrying in {RetryIntervalMs}ms");
                if (attempt >= RetryMaxAttempts)
                {
                    AgentState.Fail($"node index too small ({nodes})");
                    break;
                }
                Thread.Sleep(RetryIntervalMs);
            }
            ready.Set();   // 放 Fail/Log 之后：WaitReady 醒来时 Status 已定稿（hello 串不再有竞态）
        }) { IsBackground = true, Name = "msl-live-index" }.Start();
    }

    /// <summary>boot 期后台构建已收尾（无论成败与规模）。直调 Build（测试/重扫）只换快照、
    /// 不推进生命周期。</summary>
    public static bool Ready => ready.IsSet;

    /// <summary>是否已调用过 BeginBuild。</summary>
    public static bool Started => started;

    /// <summary>有界等待：已完成立即真；未启动立即假（不等）；构建中有界等。
    /// Handle 用它兜「boot 后不久就编译」的窗口（8s ≪ MSL 侧 10s hello 超时）。</summary>
    public static bool WaitReady(int timeoutMs) => ready.IsSet || (started && ready.Wait(timeoutMs));

    /// <summary>S2 字段表验证链（Task 11 Step 5 活体复核过 +0x64/+0x68/+0x88/+0xA0/+0xA4 与
    /// exec +0x00/+0x18）：qword==NodeSigFn 命中 → +0x64==0x00FFFFFF → +0x68→record 且
    /// record+0x00==ExecVtable → +0x80 名字可打印（≤NodeNameMax）→ 收录。</summary>
    public static int Build()
    {
        BuildDelayHook?.Invoke();
        var fresh = new Dictionary<string, NodeInfo>();
        if (AgentState.NodeSigFn != 0)   // 0 永不可能是签名 VA（扫描全零 qword 会爆量）
            foreach (var hit in Mem.ScanQword(AgentState.NodeSigFn))
                if (TryValidate(hit, out string name, out var info))
                    fresh[name] = info;
        Volatile.Write(ref byName, fresh);
        return fresh.Count;
    }

    /// <summary>单点验证（测试经 Mem.TestMap 注入合成节点直接调它）。</summary>
    internal static bool TryValidate(ulong hit, out string name, out NodeInfo info)
    {
        name = ""; info = null!;
        if (Mem.ReadU32(hit + 0x64) != 0x00FFFFFF) return false;
        ulong record = Mem.ReadU64(hit + 0x68);
        if (record == 0 || Mem.ReadU64(record) != AgentState.ExecVtable) return false;
        name = Mem.ReadCString(Mem.ReadU64(hit + 0x80), NodeNameMax);
        if (name.Length == 0) return false;
        info = new NodeInfo(hit, record,
            Mem.ReadU32(hit + 0x88), Mem.ReadU32(hit + 0x9C),
            Mem.ReadU32(hit + 0xA0), Mem.ReadU32(hit + 0xA4),
            Mem.ReadU64(record + 0x18), Mem.ReadU32(record + 0x08));
        return true;
    }

    public static bool TryGet(string name, out NodeInfo info) => Volatile.Read(ref byName).TryGetValue(name, out info!);
    public static IEnumerable<NodeInfo> All => Volatile.Read(ref byName).Values;

    /// <summary>#20 取证：崩溃转储按 record+0x20 表指针反查脚本名用（快照字典只读遍历，
    /// 键=名）。与 All 同源，只是带出名字。</summary>
    internal static IEnumerable<KeyValuePair<string, NodeInfo>> AllNamed => Volatile.Read(ref byName);
    public static int Count => Volatile.Read(ref byName).Count;

    internal static void ResetForTest()
    {
        lock (startLock) started = false;
        ready.Reset();
        byName = new Dictionary<string, NodeInfo>();
        MinNodes = 30000;
        RetryMaxAttempts = 60;
        RetryIntervalMs = 5000;
    }
}
