using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>两阶段应用引擎（spec D4 全成或全弃）。
/// Phase 1（pipe 线程，Enqueue）：逐 op resolve（NodeIndex 直名优先；#17 实证：wrapper 根
/// 按绑定创建无节点 → 回退 "gml_Script_"+名经子把住共享 buffer；直名命中子条目才拒——
/// 子 op 不该存在，CodeDiffer 已滤，防御性守卫）→ validate（帧容量
/// ≤ 语义：frameOwner = 共享 BufPtr 的最小 StartOff 别名子（S2④：调子=从偏移 4 进入执行
/// 子体——子才是帧主；多子 wrapper 取最小偏移的主子），无别名子则节点自身；op.LocalsCount
/// ≤ frameOwner.Locals——交换不 patch record+0x0C，帧容量以 boot 值为上限，载荷局部数
/// 超容量即拒（v1 已知限制：locals 数增长不热更）；另核对全部别名的 node+0xA0 ==
/// record+0x0C 镜像一致——交换面完整性；#19 自证：已特化旧记录按旧 buffer 重算 handler
/// 表/pcmap 与活表比对，不等即拒）→ 编码（Translator/BcEncoder，Reject 归
/// validate）→ TableBuilder 建 handler 表/pcmap（#19：线程化代码 VM 派发走 record+0x20
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
        public ulong NewBuf;
        public ulong NewTable;   // #19：handler 表（record+0x20）与 pcmap（+0x28）随 buffer 同换
        public ulong NewMap;
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
            var all = batch.Ops.Select((o, i) => i == failAt ? failReceipt : new OpReceipt
            {
                Seq = o.Seq, Entry = o.Entry, Stage = "validate",
                Reason = $"batch aborted: {failReceipt.Entry} ({failReceipt.Reason})",
            }).ToList();
            AgentState.Log($"apply batch {batch.BatchSeq} aborted at {failReceipt.Entry}: {failReceipt.Reason}");
            return all;
        }
        lock (gate)
        {
            if (pending.Count > 0)
            {
                // MSL 顺序收发（等回执才发下一批），正常到不了这里——防御性拒收，防回执串批
                return batch.Ops.Select(o => new OpReceipt
                {
                    Seq = o.Seq, Entry = o.Entry, Stage = "validate",
                    Reason = "previous batch still pending (waiting for next frame)",
                }).ToList();
            }
            foreach (var p in prepared) pending.Enqueue(p);
            pendingBatchSeq = batch.BatchSeq;
        }
        AgentState.Log($"apply batch {batch.BatchSeq}: {prepared.Count} ops queued for next frame");
        return null;
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
        if (NodeIndex.TryGet(op.Entry, out var node))
        {
            if (node.StartOff != 0)
            { receipt.Reason = $"alias child entry (startOff={node.StartOff}), not swappable"; return null; }
        }
        else if (!NodeIndex.TryGet("gml_Script_" + op.Entry, out node))
        { receipt.Reason = "node not found"; return null; }

        receipt.Stage = "validate";
        // 别名集 = 共享 buffer 的全部节点（S2④：根+子双记录同指 BUF）；帧主 = 最小
        // StartOff 的别名子（执行面），无子则节点自身（普通事件条目）
        var aliases = NodeIndex.All.Where(n => n.BufPtr == node.BufPtr).ToList();
        var frameOwner = aliases.Where(n => n.StartOff != 0).OrderBy(n => n.StartOff).FirstOrDefault() ?? node;
        // 容量语义：交换不 patch record+0x0C（零运行时 patch），帧以 boot 值定容——
        // 载荷局部数 ≤ 容量即安全（帧偏大无害），超容量 = 溢出风险，拒
        if (op.LocalsCount < 0 || (uint)op.LocalsCount > frameOwner.Locals)
        { receipt.Reason = $"locals mismatch: payload {op.LocalsCount} > frame capacity {frameOwner.Locals}"; return null; }
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
        try { bytes = BcEncoder.Encode(op.Instructions, new Translator(op)); }
        catch (TranslationRejectException ex) { receipt.Reason = ex.Message; return null; }
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
            Op = op, Bytes = bytes, Records = records,
            NewBuf = newBuf, NewTable = newTable, NewMap = newMap, Receipt = receipt,
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
    }
}
