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

    /// <summary>boot 期（OnInitGML，游戏建完 exec 节点后的最早安全点）后台构建索引。
    /// fix-loop #9：原先在首连 SelfCheck 里同步跑——真机实测 26min43s（扫全部提交私有 RW 区 +
    /// 38K 命中散读，游戏 3.3GB 工作集被扫描逐出后每次解引用 ~4ms 页错误），hello 被堵到
    /// MSL 10s 超时之后才发出。boot 期内存新鲜（data.win 刚加载、页多驻留），构建在秒级；
    /// 就算慢，握手路径也不再有人等它——hello 每连接快照现状（PipeServer.Handle）。</summary>
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
            try
            {
                int nodes = Build();
                if (nodes < 30000) AgentState.Fail($"node index too small ({nodes})");   // 规模护栏在唯一知道总数的构建线程上报
                AgentState.Log($"node index built: {nodes} nodes in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                AgentState.Fail("node index build failed: " + ex.Message);
                AgentState.Log("node index build failed: " + ex);
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
    /// record+0x00==ExecVtable → +0x80 名字可打印（≤128B）→ 收录。</summary>
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
        name = Mem.ReadCString(Mem.ReadU64(hit + 0x80), 128);
        if (name.Length == 0) return false;
        info = new NodeInfo(hit, record,
            Mem.ReadU32(hit + 0x88), Mem.ReadU32(hit + 0x9C),
            Mem.ReadU32(hit + 0xA0), Mem.ReadU32(hit + 0xA4),
            Mem.ReadU64(record + 0x18), Mem.ReadU32(record + 0x08));
        return true;
    }

    public static bool TryGet(string name, out NodeInfo info) => Volatile.Read(ref byName).TryGetValue(name, out info!);
    public static IEnumerable<NodeInfo> All => Volatile.Read(ref byName).Values;
    public static int Count => Volatile.Read(ref byName).Count;

    internal static void ResetForTest()
    {
        lock (startLock) started = false;
        ready.Reset();
        byName = new Dictionary<string, NodeInfo>();
    }
}
