using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>两阶段应用引擎（spec D4 全成或全弃）。
/// Phase 1（pipe 线程，Enqueue）：逐 op resolve（NodeIndex 直名优先；#17 实证：wrapper 根
/// 按绑定创建无节点 → 回退 "gml_Script_"+名经子把住共享 buffer；直名命中子条目才拒——
/// 子 op 不该存在，CodeDiffer 已滤，防御性守卫）→ validate（#30 局部门 + #33 count patch：
/// frameOwner = 共享 BufPtr 的最小 StartOff 别名子（S2④：调子=从偏移 4 进入执行子体——
/// 子才是帧主；多子 wrapper 取最小偏移的主子），无别名子则节点自身；容量上限已按
/// findings 2026-09-04 §六① 全读者核验删除（&gt;4096 荒谬值护栏保留）；载荷用局部而
/// boot 帧局部数 0 时不再拒——#33 改为 commit 窗口把帧计数 0→N patch（invoker 每次调用
/// 读 node+0xA0 新建容器，count 唯一读者是激活门，详见 Prepare 内注）；
/// 另核对全部别名的 node+0xA0 == record+0x0C 镜像一致——交换面完整性；#19 自证：已特化
/// 旧记录按旧 buffer 重算 handler 表/pcmap 与活表比对，不等即拒）→ 编码（Translator/
/// BcEncoder，Reject 归 validate；#30：l: miss 由 Translator 借位 id，明细进回执）→
/// TableBuilder 建 handler 表/pcmap（#19：线程化代码 VM 派发走 record+0x20
/// 表，表/pcmap 必须随 buffer 同换）→ VirtualAlloc(RW) 写新 buffer+表+pcmap（失败归
/// validate 提前量——commit 设计为不可失败）→ 收集共享旧 buffer 的全部执行记录
/// （S2④ 父子别名同 buffer；#19 全量交换面 = +0x08/+0x18/+0x20/+0x28 四字段）。
/// 任一 op 失败 → 整批不入队：已分配 buffer 留置不 free（与旧 buffer 同策），一个都不换
/// （半应用 = 新旧代码互相调用 = 状态不一致），失败 op 记真实原因、其余记 batch aborted。
/// Phase 2（游戏线程，msl_live_apply thunk → Pump）：先补上一批的 pendingRestore（trigger 配对
/// restore，下一帧此刻 RunGml 已跑完一帧），再逐 prepared 四字段写（+0x08→+0x18→+0x28→
/// +0x20，buffer 先于表 = 安全窗，见 TableBuilder.WriteRecord；游戏线程无并发读者；
/// 旧 buffer 永不释放）。restore op 不当帧换入——记为 pendingRestore，
/// 其 receipt 在本批回执里即记 Ok（它保证下一帧执行；游戏若先关则整局皆休）。回执只置
/// 出站信箱，pipe 线程轮询转发（游戏线程写 pipe 可能阻塞 VM；MSL 30s 超时兜底=诚实超时）。</summary>
public static class ApplyEngine
{
    sealed class Prepared
    {
        public OpMsg Op = null!;
        public byte[] Bytes = null!;
        public List<ulong> Records = null!;
        public List<NodeInfo> Aliases = null!;   // #33：count patch 要写别名节点侧（node+0xA0）
        public ulong NewBuf;
        public ulong NewTable;   // #19：handler 表（record+0x20）与 pcmap（+0x28）随 buffer 同换
        public ulong NewMap;
        public ulong StrgTable;  // #34：StrgAppendix 新偏移表（batch 共享；Pump 首个非零时换槽一次）
        public uint LocalsPatch; // #33：0 = 无 patch；否则 commit 窗口写的帧局部数
        public OpReceipt Receipt = null!;
    }

    static readonly object gate = new();
    static readonly Queue<Prepared> pending = new();
    static readonly List<OpReceipt> receipts = new();
    static Prepared? pendingRestore;
    static BatchReceipt? outbox;
    static int pendingBatchSeq = -1;

    internal static void ResetForTest()
    {
        lock (gate)
        {
            pending.Clear(); receipts.Clear();
            pendingRestore = null; outbox = null; pendingBatchSeq = -1;
        }
    }

    /// <summary>Phase 1。返回 null = 整批通过已入队（回执待 Pump）；否则 = 整批放弃的最终回执
    /// （调用方立即回给 MSL，无 Pump 必要）。slot = 通用槽解析器（生产 = 运行时读游戏槽表，
    /// 测试注入假槽——TableBuilder 的特化复刻真源）。</summary>
    public static List<OpReceipt>? Enqueue(BatchMsg batch, Func<int, ulong>? slot = null)
    {
        slot ??= TableBuilder.RuntimeSlot;
        HarvestCalibrations(batch);   // #21：变量 id 真源收割必须先于任何 op 翻译
        var prepared = new List<Prepared>();
        int failAt = -1;
        OpReceipt? failReceipt = null;
        for (int i = 0; i < batch.Ops.Count; i++)
        {
            var op = batch.Ops[i];
            var receipt = new OpReceipt { Seq = op.Seq, Entry = op.Entry, Stage = "resolve" };
            var p = Prepare(op, receipt, slot);
            if (p == null) { failAt = i; failReceipt = receipt; break; }
            prepared.Add(p);
        }
        if (failReceipt != null)
        {
            // spec D4：整批弃——prepared 里的 buffer 已分配但永不换入（留置，与旧 buffer 同策）
            StrgAppendix.DiscardPending();   // #34：弃批——半物化 id 不得残留去重表
            var all = batch.Ops.Select((o, i) => i == failAt ? failReceipt : new OpReceipt
            {
                Seq = o.Seq, Entry = o.Entry, Stage = "validate",
                Reason = $"batch aborted: {failReceipt.Entry} ({failReceipt.Reason})",
            }).ToList();
            AgentState.Log($"apply batch {batch.BatchSeq} aborted at {failReceipt.Entry}: {failReceipt.Reason}");
            return all;
        }
        // #34 防御性拒收前置：先确认能入队再物化（若先物化后拒收，committed 已转正而表
        // 永不换入——下批同字符串复用幽灵 id → push 越界读）。pending.Count 只在 Enqueue
        // （pipe 线程单线程）增加、Pump 只减——本检查过后到入队前不可能变非零。
        lock (gate)
        {
            if (pending.Count > 0)
            {
                // MSL 顺序收发（等回执才发下一批），正常到不了这里——防御性拒收，防回执串批
                StrgAppendix.DiscardPending();   // #34：弃本批预分配（未物化，仅记账）
                return batch.Ops.Select(o => new OpReceipt
                {
                    Seq = o.Seq, Entry = o.Entry, Stage = "validate",
                    Reason = "previous batch still pending (waiting for next frame)",
                }).ToList();
            }
        }
        // #34：整批通过——新字符串物化（块+新表，pipe 线程；换槽在 Pump 的 commit 窗）。
        // 失败（偏移域外/分配失败）按整批弃处理（Materialize 内部已弃 pending）。
        ulong strgTable = 0;
        try { (strgTable, _) = StrgAppendix.Materialize(); }
        catch (TranslationRejectException ex)
        {
            var all = batch.Ops.Select(o => new OpReceipt
            {
                Seq = o.Seq, Entry = o.Entry, Stage = "validate",
                Reason = $"batch aborted: string appendix ({ex.Message})",
            }).ToList();
            AgentState.Log($"apply batch {batch.BatchSeq} aborted: string appendix: {ex.Message}");
            return all;
        }
        lock (gate)
        {
            foreach (var p in prepared) { p.StrgTable = strgTable; pending.Enqueue(p); }
            pendingBatchSeq = batch.BatchSeq;
        }
        AgentState.Log($"apply batch {batch.BatchSeq}: {prepared.Count} ops queued for next frame");
        return null;
    }

    /// <summary>#21 校准语料收割（_ally_hp 事故的修复核心）：对 batch.CalibOps 逐个——
    /// 二段解析取活 buffer（与 Prepare 同源的直名 → "gml_Script_"+名 回退；语料只读不换，
    /// StartOff≠0 的别名子合法——BufPtr 即共享基址，MSL 侧保证语料是 ParentEntry==null 的
    /// 根流，walk 从基址起对齐）→ VarCalibrator.Harvest 读回 runner 回填的变量 id。
    /// 收割失败只记日志不拒批——覆盖缺口由 Translator 在编码处 fail-closed（未校准即拒），
    /// 与 spec D4 的整批语义一致（拒绝发生在 Prepare，回执照常带原因）。
    /// 时序安全性：本批 swap 的换入发生在 Pump（下一帧），此刻全部活 buffer 仍是 runner
    /// 原始/上次已校准安装的内容——收割永远先于本批任何换入。</summary>
    static void HarvestCalibrations(BatchMsg batch)
    {
        if (batch.CalibOps.Count == 0) return;
        int ok = 0;
        foreach (var op in batch.CalibOps)
        {
            // 语料解析：直名 → gml_Script_ 裸名回退（gml_GlobalScript_ 根须剥前缀——子节点名是
            // 裸名形态；只读不换，StartOff≠0 别名子合法：BufPtr=共享基址，根流 walk 从基址对齐）。
            // 注意与 Prepare 的回退不同：那是对 swap 目标的既有行为（#17），此处是语料专用。
            // TryCorpusLive（E2E 后）：子条目直名也进语料（子区段对照），不再整 buffer 错位跳过
            if (!TryCorpusLive(op, out _, out var live, out string why))
            { AgentState.Log($"calib '{op.Entry}': {why}"); continue; }
            var errors = new List<string>();
            if (!VarCalibrator.Harvest(op, live, errors))
                AgentState.Log($"calib '{op.Entry}': harvest partial: {string.Join(" | ", errors.Take(2))}");
            else ok++;
        }
        AgentState.Log($"calib: {ok}/{batch.CalibOps.Count} corpus entries harvested clean");
    }

    internal static string BareName(string entry) =>
        entry.StartsWith("gml_GlobalScript_") ? entry.Substring("gml_GlobalScript_".Length) : entry;

    /// <summary>proof / calib 语料共用的「op → (节点, 活字节)」解析（与 Prepare 的 swap 语义
    /// 不同：只读验证，不做交换面检查）。三种形态：
    /// ① 直名命中 StartOff=0（普通条目/vanilla 根）→ 整 buffer；
    /// ② 直名命中 StartOff≠0（gml_Script_* 子条目——ProofBuilder 不过滤子条目，语料必含）
    ///   → 共享 buffer 的 [StartOff, +ΣByteSize) 子区段（载荷 = 子自己的指令流；尾部 wrapper
    ///   字节不在对照面内。载荷长先算后切，避开「先编码才知道长」的循环依赖）；
    /// ③ 直名 miss（wrapper 裸根，#17 无节点）→ gml_Script_+裸名回退（#22 剥前缀）→ 整 buffer
    ///   （根载荷 = 整流）。失败返 false（why 带文案），调用方决定 proof 计败 / calib 跳过。</summary>
    internal static bool TryCorpusLive(OpMsg op, out NodeInfo node, out byte[] live, out string why)
    {
        node = null!; live = Array.Empty<byte>(); why = "";
        if (NodeIndex.TryGet(op.Entry, out var direct))
        {
            node = direct;
            byte[] whole = Mem.ReadBytes(node.BufPtr, (int)node.BufLen);
            if (whole.Length == 0) { why = "empty/unreadable live buffer"; return false; }
            if (direct.StartOff == 0) { live = whole; return true; }
            int payloadLen = 0;
            try { foreach (var sem in op.Instructions) payloadLen += BcEncoder.ByteSize(sem); }
            catch (Exception ex) { why = ex.Message; return false; }
            if (direct.StartOff + payloadLen > whole.Length)
            { why = $"sub-range {direct.StartOff}+{payloadLen} overruns live {whole.Length}B"; return false; }
            live = new byte[payloadLen];
            Array.Copy(whole, (int)direct.StartOff, live, 0, payloadLen);
            return true;
        }
        if (!NodeIndex.TryGet("gml_Script_" + BareName(op.Entry), out node))
        { why = "node not found"; return false; }
        live = Mem.ReadBytes(node.BufPtr, (int)node.BufLen);
        if (live.Length == 0) { why = "empty/unreadable live buffer"; return false; }
        return true;
    }

    /// <summary>单 op Phase 1 链。失败 → 填好 receipt（Stage/Reason）返回 null。</summary>
    static Prepared? Prepare(OpMsg op, OpReceipt receipt, Func<int, ulong> slot)
    {
        // #17 外扫实证（nodescan 普查）：运行时 exec 节点按「绑定」创建（SCPT/FUNC/事件/
        // GlobalInit 指向谁谁才有节点），不按 Code 条目——wrapper 根（槽/loader/函数声明
        // 裸根）刻意不入 GlobalInit 且无人指向 → 0 节点；SCPT/FUNC 都指向子 → 索引只有
        // gml_Script_+名（StartOff=4，record 覆盖整 buffer，BufPtr=共享 buffer 基址）。
        // op.Entry=裸根名直名 miss → 回退子名把住共享 buffer：子是句柄不是 op 目标，
        // alias-child 守卫只对直名命中生效（直名命中且 StartOff≠0 = op 目标本身是子条目
        // ——CodeDiffer 已滤，防御性守卫，见 Enqueue_AliasChildEntry_ResolveFail）。
        // #22：回退须先剥 gml_GlobalScript_ 前缀（改既有 vanilla 脚本时 op.Entry=根名，
        // 不剥会拼出 gml_Script_gml_GlobalScript_ 错名误拒——见
        // Enqueue_SwapOp_GlobalScriptRoot_ViaBareScriptChild）。
        if (NodeIndex.TryGet(op.Entry, out var node))
        {
            if (node.StartOff != 0)
            { receipt.Reason = $"alias child entry (startOff={node.StartOff}), not swappable"; return null; }
        }
        else if (!NodeIndex.TryGet("gml_Script_" + BareName(op.Entry), out node))
        { receipt.Reason = "node not found"; return null; }

        receipt.Stage = "validate";
        // 别名集 = 共享 buffer 的全部节点（S2④：根+子双记录同指 BUF）；帧主 = 最小
        // StartOff 的别名子（执行面），无子则节点自身（普通事件条目）
        var aliases = NodeIndex.All.Where(n => n.BufPtr == node.BufPtr).ToList();
        var frameOwner = aliases.Where(n => n.StartOff != 0).OrderBy(n => n.StartOff).FirstOrDefault() ?? node;
        // #30 容量语义重塑（RE findings 2026-09-04 §六① 全读者核验）：count(+0x5C) 唯一
        // 读者是激活门（==0 → 局部访问整条静默跳过）；容器 map 是 find-or-create（0x1400accd0），
        // 未知 id 当场建槽不越界；GC/析构/struct 主线全不看 count——「载荷 ≤ boot 容量」
        // 上限删除（用户加局部 = 日常最常见编辑，旧语义把整类编辑拒掉）。交换仍不 patch
        // record+0x0C（零运行时 patch），唯一真实约束：boot 帧局部数为 0 时不得引入局部
        // 访问（激活门关死 → 全部静默错值）。另留荒谬值护栏（裁决原文「如 >4096 拒」）。
        if (op.LocalsCount < 0 || op.LocalsCount > 4096)
        { receipt.Reason = $"locals count out of range: {op.LocalsCount}"; return null; }
        // #30-D：直检载荷 sems——旧编译器对函数形子条目/裸条目恒报 LocalsCount=0（谎，
        // probe6 实证），只信计数会把「-7 引用 + 谎 0」的 patch 资格漏判
        bool payloadUsesLocals = op.Instructions.Any(s => s.Inst == -7 && s.Var != null);
        // #33（真机 scr_console_getseed 整批拒 + E2E array_local 确定复现）：boot 帧局部数 0
        // + 载荷用局部 → 不再整批拒，commit 窗口把帧计数 0→N patch。RE findings
        // 2026-09-04 §二/§六① 全读者核验：invoker（0x14028B5C0）每次调用读 node+0xA0
        // 新建局部容器，count(+0x5C) 唯一读者是激活门（==0 → 局部访问整条静默跳过）——
        // 不约束 map 容量（find-or-create）、不约束帧栈分配、GC/析构/struct 主线全不看。
        // N=max(LocalsCount,1)（MSL 救援层对函数形谎 0 已兜底 ≥1；count 除非零外无语义）。
        // 镜像一致性（下方检查）先核后 patch：全别名 node+0xA0 与 record+0x0C 同批写，
        // 否则下一次推送会被我们自己的镜像检查拒掉。
        uint localsPatch = 0;
        if ((payloadUsesLocals || op.LocalsCount > 0) && frameOwner.Locals == 0)
            localsPatch = (uint)Math.Max(op.LocalsCount, 1);
        // 镜像完整性：全部别名的 node+0xA0 与各自 record+0x0C 必须一致（交换面双侧真源同源）
        foreach (var a in aliases)
        {
            uint recordLocals = Mem.ReadU32(a.Record + 0x0C);
            if (recordLocals != a.Locals)
            { receipt.Reason = $"locals mismatch: node {a.Locals} != record+0x0C {recordLocals}"; return null; }
        }
        // #19 真机自证（fail-closed）：任一共享记录已特化（+0x20≠0）就按旧 buffer 重算
        // 表/pcmap 与活表逐字节比对——不等 = 特化规则复刻错了，换入即野派发，整批拒
        foreach (var a in aliases)
            if (!TableBuilder.SelfProofRecord(a.Record, slot, out var why))
            { receipt.Reason = $"specialization self-proof: {why}"; return null; }

        byte[] bytes;
        var translator = new Translator(op);
        try { bytes = BcEncoder.Encode(op.Instructions, translator); }
        catch (TranslationRejectException ex) { receipt.Reason = ex.Message; return null; }
        // #30：借位明细进回执（l: 新局部名 → 既有范围内 id；MSL 侧日志可见）
        foreach (var (name, id) in translator.BorrowedLocals)
            receipt.BorrowedIds.Add($"l:{name}→{id}");
        // #34：热分配字符串明细进回执（内容 → 新 id）
        foreach (var (content, id) in translator.StrgAppended)
            receipt.StrgAppended.Add($"{content}→{id}");
        if (bytes.Length == 0) { receipt.Reason = "empty payload"; return null; }

        // #19 线程化代码 VM：派发走 record+0x20 handler 表（首执行按 buffer 懒构建）——
        // 表/pcmap 必须与 buffer 同换，且在 Phase 1 构建+分配（commit 设计为不可失败）
        byte[] table, pcmap;
        try { (table, pcmap) = TableBuilder.Build(bytes, slot); }
        catch (TranslationRejectException ex) { receipt.Reason = ex.Message; return null; }

        ulong newBuf = Mem.AllocRW(bytes);
        ulong newTable = Mem.AllocRW(table);
        ulong newMap = Mem.AllocRW(pcmap);
        if (newBuf == 0 || newTable == 0 || newMap == 0)
        { receipt.Reason = "VirtualAlloc failed"; return null; }   // commit 不可失败 → 提前量归 validate

        var records = aliases.Select(n => n.Record).ToList();
        if (records.Count == 0) { receipt.Reason = "no execution records share the buffer"; return null; }
        return new Prepared
        {
            Op = op, Bytes = bytes, Records = records, Aliases = aliases,
            NewBuf = newBuf, NewTable = newTable, NewMap = newMap,
            LocalsPatch = localsPatch, Receipt = receipt,
        };
    }

    /// <summary>Phase 2（游戏线程）。restore 优先，然后整批指针写；回执置出站信箱。</summary>
    public static void Pump()
    {
        Prepared? restore = null;
        lock (gate)
        {
            restore = pendingRestore;
            pendingRestore = null;
        }
        if (restore != null) CommitOne(restore);   // 上一批 trigger 的配对 restore：此刻已跑完一帧

        List<OpReceipt> done;
        int batchSeq;
        int ms;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (gate)
        {
            if (pending.Count == 0) return;
            // #34：字符串偏移表换槽（commit 窗，游戏线程无并发读者）——新表在 Enqueue 已
            // 完整就位，本写是唯一动作（不可失败）；批内只换一次（prepared 共享同表）
            ulong strgTable = pending.Peek().StrgTable;
            if (strgTable != 0) StrgAppendix.SwapTable(strgTable);
            while (pending.Count > 0)
            {
                var p = pending.Dequeue();
                if (p.Op.Kind == "restore") pendingRestore = p;   // 不当帧换入，保证下一帧
                else CommitOne(p);
                p.Receipt.Ok = true;
                p.Receipt.Stage = "commit";
                receipts.Add(p.Receipt);
            }
            done = new List<OpReceipt>(receipts);
            receipts.Clear();
            batchSeq = pendingBatchSeq;
            pendingBatchSeq = -1;
        }
        sw.Stop();
        ms = (int)sw.ElapsedMilliseconds;
        lock (gate)
            outbox = new BatchReceipt { BatchSeq = batchSeq, AllOk = true, Ops = done, ApplyMs = ms };
        AgentState.Log($"apply batch {batchSeq}: {done.Count} ops committed in {ms}ms");
    }

    /// <summary>pipe 线程轮询取回执（取走即清）。</summary>
    public static BatchReceipt? TryTakeReceipt()
    {
        lock (gate) { var r = outbox; outbox = null; return r; }
    }

    static void CommitOne(Prepared p)
    {
        foreach (var record in p.Records)
            TableBuilder.WriteRecord(record, (uint)p.Bytes.Length, p.NewBuf, p.NewTable, p.NewMap);
        // #33 激活门 count patch：与指针写同批（游戏线程内无并发读者）；逐别名
        // node+0xA0 + record+0x0C 双侧写保持镜像不变量（见 Prepare #33 注）。
        // 只写当前计数为 0 的别名——已开的门不重写（父根/vanilla 根的计数另有语义：
        // 根在 GlobalInit 只跑一次，帧主是子；多子 wrapper 各子都是自己的帧，全开）
        if (p.LocalsPatch != 0)
        {
            foreach (var a in p.Aliases)
            {
                if (Mem.ReadU32(a.Node + 0xA0) != 0) continue;
                Mem.WriteU32(a.Node + 0xA0, p.LocalsPatch);
                Mem.WriteU32(a.Record + 0x0C, p.LocalsPatch);
            }
            p.Receipt.LocalsPatched = p.LocalsPatch;
            AgentState.Log($"apply '{p.Op.Entry}': frame locals count 0 -> {p.LocalsPatch} (activation gate patch)");
        }
    }
}
