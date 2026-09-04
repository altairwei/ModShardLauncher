using System.Text;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>ApplyEngine 两阶段语义（合成内存）：Enqueue（pipe 线程侧 Phase 1）→ Pump（游戏线程侧
/// Phase 2）。钉 spec D4 全成或全弃、trigger/restore 跨帧语义、出站信箱回执、防御性拒收。
/// 计划 Files 清单漏列本测试文件——按项目 TDD 纪律补上（断言全部针对计划语义，未新增产品行为）。</summary>
public class ApplyEngineTests : IDisposable
{
    const ulong SIG = 0x1406BE508;
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    const ulong Node = 0x10100, Record = 0x10800, Name = 0x10C00, Buf = 0x11000;
    const ulong AliasNode = 0x10200, AliasRecord = 0x10900, AliasName = 0x10D00;

    public ApplyEngineTests()
    {
        Mem.TestMap = new byte[0x2000];
        Mem.TestBase = Base;
        Mem.TestAllocs.Clear();
        AgentState.ResetForTest();
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        Mem.TestAllocs.Clear();
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
        AgentState.ResetForTest();
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static uint R32(ulong addr) => BitConverter.ToUInt32(Mem.TestMap!, (int)(addr - Base));
    static ulong R64(ulong addr) => BitConverter.ToUInt64(Mem.TestMap!, (int)(addr - Base));

    /// <summary>种节点（locals=2：节点 +0xA0 与执行记录 +0x0C 双镜像一致——validate 两处核对）。</summary>
    static void PlantNode(ulong node, ulong record, ulong nameAt, ulong bufAt, string name,
        uint locals = 2, uint startOff = 0, byte[]? live = null)
    {
        W64(node, SIG);
        W32(node + 0x64, 0x00FFFFFF);
        W64(node + 0x68, record);
        W64(record, EXEC);
        W32(record + 0x08, (uint)(live?.Length ?? 8));
        W32(record + 0x0C, locals);             // 执行记录 locals 镜像（validate 第二真源）
        W64(record + 0x18, bufAt);
        W64(node + 0x80, nameAt);
        Encoding.ASCII.GetBytes(name).CopyTo(Mem.TestMap!, (int)(nameAt - Base));
        W32(node + 0x88, 1234);
        W32(node + 0x9C, startOff);
        W32(node + 0xA0, locals);
        (live ?? new byte[8]).CopyTo(Mem.TestMap!, (int)(bufAt - Base));
    }

    /// <summary>popz 链载荷（无引用，编码自洽；n 条 = 4n 字节）。</summary>
    static OpMsg PopzOp(string entry, int n, string kind = "swap", int seq = 0) => new()
    {
        Seq = seq, Kind = kind, Entry = entry, LocalsCount = 2,
        Instructions = Enumerable.Range(0, n)
            .Select(_ => new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable }).ToList(),
    };

    static BatchMsg Batch(params OpMsg[] ops) => new() { BatchSeq = 7, Ops = ops.ToList() };

    [Fact]
    public void Enqueue_ThenPump_CommitsEveryRecordSharingBuffer()
    {
        // 父 + 别名子共享同一 buffer（S2④）——交换面 = 全部共享记录（S3）
        PlantNode(Node, Record, Name, Buf, "entry_a");
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", startOff: 4);
        Assert.Equal(2, NodeIndex.Build());
        ulong oldBuf = R64(Record + 0x18);

        var receipts = ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 3)));
        Assert.Null(receipts);                      // Phase 1 全过 → 入队等 Pump
        Assert.Equal(oldBuf, R64(Record + 0x18));   // Phase 1 绝不换指针

        ApplyEngine.Pump();
        foreach (var rec in new[] { Record, AliasRecord })
        {
            Assert.Equal(12u, R32(rec + 0x08));                 // 3 × popz = 12B
            ulong ptr = R64(rec + 0x18);
            Assert.True(Mem.TestAllocs.ContainsKey(ptr));
            Assert.Equal(BcEncoder.Encode(PopzOp("entry_a", 3).Instructions, new Translator(new OpMsg())),
                Mem.TestAllocs[ptr]);
        }
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        Assert.Equal(7, receipt.BatchSeq);
        var op = Assert.Single(receipt.Ops);
        Assert.True(op.Ok);
        Assert.Equal("commit", op.Stage);
        Assert.Null(ApplyEngine.TryTakeReceipt());  // 取走即清
    }

    [Fact]
    public void Enqueue_NodeMissing_ResolveFail_ImmediateReceipt()
    {
        NodeIndex.Build();
        var receipts = ApplyEngine.Enqueue(Batch(PopzOp("missing", 1)));
        var r = Assert.Single(receipts!);
        Assert.Equal("resolve", r.Stage);
        Assert.Equal("node not found", r.Reason);
        Assert.Equal("missing", r.Entry);
        ApplyEngine.Pump();                          // 无队可泵
        Assert.Null(ApplyEngine.TryTakeReceipt());
    }

    /// <summary>#30：容量上限删除——RE 全读者核验（findings 2026-09-04 §六①：count(+0x5C)
    /// 唯一读者是激活门；容器 map 是 find-or-create，未知 id 当场建槽；GC/析构/struct 主线
    /// 全不看 count）。载荷局部数 &gt; boot 容量从此放行——用户加局部变量 = 日常最常见编辑，
    /// 旧「≤ 容量」语义把 scr_console_help 加 4 局部整个拒掉 = #30 立项根因。旧用例
    /// Enqueue_LocalsMismatch_ValidateFail 钉的拒批形态被 #30 有意取代；剩余真实约束见
    /// <see cref="Enqueue_PayloadLocalsButBootZero_Rejected"/>。</summary>
    [Fact]
    public void Enqueue_PayloadExceedsBootLocals_Passes()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 2);
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 5;                          // 节点/记录都是 2，载荷 5——旧语义在此拒
        Assert.Null(ApplyEngine.Enqueue(Batch(op)));
        ApplyEngine.Pump();
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        Assert.NotEqual(Buf, R64(Record + 0x18));    // 确已换入
    }

    /// <summary>#30 新形态（唯一真实约束）：boot locals == 0 → 容器 count=0 → 激活门把
    /// 全部局部访问静默跳过（findings §一：count==0 则 push/pop 整条 no-op）——载荷带局部
    /// = 全部静默错值，拒。对照：载荷 0 局部 + boot 0 → 放行（无局部访问可跳）。</summary>
    [Fact]
    public void Enqueue_PayloadLocalsButBootZero_Rejected()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 0);
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 2;
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("boot frame locals", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));       // 未换

        var none = PopzOp("entry_a", 1);             // 对照：0 局部载荷在 0 局部帧上放行
        none.LocalsCount = 0;
        Assert.Null(ApplyEngine.Enqueue(Batch(none)));
        ApplyEngine.Pump();
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
    }

    /// <summary>#30-D agent 侧防御：Gate 2 不得只信 op.LocalsCount——旧编译器对函数形子条目/
    /// 裸条目恒报 0（谎，probe6 实证），MSL 侧救援已兜底 ≥1，但接口边界仍须直检载荷 sems：
    /// 载荷含 -7 局部引用 + 谎 LocalsCount=0 + boot 帧局部 0 → 拒。借位池非空时谎 0 会把
    /// 拒批完全绕过（借位成功 → 静默换入 → 激活门关死 → 局部访问整条静默错值）。</summary>
    [Fact]
    public void Enqueue_PayloadLocalRefsLyingZeroCount_BootZero_Rejected()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 0);
        NodeIndex.Build();
        // 借位池非空（模拟表既有 id）——无 sems 直检时谎 0 载荷会借位成功被静默放行
        AgentState.VarMap = new Dictionary<string, int> { ["i:some_var"] = 0, ["l:some_local"] = 1 };
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 0;                          // 谎值（旧编译器函数形子条目恒 0）
        op.Instructions.Add(new SemInstruction
            { Kind = BcEncoder.OpPop, T1 = BcEncoder.TVariable, Inst = -7, Var = "_lying_zero_x" });
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("boot frame locals", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));       // 未换
    }

    /// <summary>#30 荒谬值护栏：负数拒；&gt; 4096 拒（真实函数局部数个位数量级，四位数 =
    /// 上游模型错——裁决原文「如 &gt;4096 拒」）。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Enqueue_AbsurdOrNegativeLocals_Rejected(int n)
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 2);
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = n;
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("locals count out of range", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));       // 未换
    }

    [Fact]
    public void Enqueue_NonBootString_ValidateFail()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        NodeIndex.Build();
        var op = new OpMsg
        {
            Seq = 0, Kind = "swap", Entry = "entry_a", LocalsCount = 2,
            Strings = { new StrRef { Content = "fresh", StrgIndex = -1 } },
            Instructions =
            {
                new SemInstruction { Kind = BcEncoder.OpPush, T1 = BcEncoder.TString, Str = "fresh" },
                new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
            },
        };
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("non-boot string", r.Reason);
    }

    [Fact]
    public void BatchAbort_FirstFailureAbortsAll_NothingCommitted()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        NodeIndex.Build();
        ulong oldBuf = R64(Record + 0x18);

        var receipts = ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 3, seq: 0), PopzOp("missing", 1, seq: 1)));
        Assert.Equal(2, receipts!.Count);
        Assert.Equal("validate", receipts[0].Stage);              // 已过 Phase 1 的 op 也陪葬
        Assert.Contains("batch aborted: missing (node not found)", receipts[0].Reason);
        Assert.Equal("resolve", receipts[1].Stage);               // 失败 op 记真实详情
        Assert.Equal("node not found", receipts[1].Reason);
        Assert.Equal(oldBuf, R64(Record + 0x18));                 // 半应用禁止：一个都不换
        ApplyEngine.Pump();
        Assert.Null(ApplyEngine.TryTakeReceipt());
    }

    [Fact]
    public void TriggerRestore_RestoreDefersToNextPump()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        NodeIndex.Build();

        var trigger = PopzOp("entry_a", 3, kind: "trigger", seq: 0);
        var restore = PopzOp("entry_a", 1, kind: "restore", seq: 1);
        Assert.Null(ApplyEngine.Enqueue(Batch(trigger, restore)));

        ApplyEngine.Pump();   // 帧 1：trigger 换入，restore 挂起
        ulong trigBuf = R64(Record + 0x18);
        Assert.Equal(12u, R32(Record + 0x08));
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        Assert.Equal(2, receipt.Ops.Count);
        Assert.All(receipt.Ops, o => { Assert.True(o.Ok); Assert.Equal("commit", o.Stage); });

        ApplyEngine.Pump();   // 帧 2：空队列，但 pendingRestore 先落
        ulong restBuf = R64(Record + 0x18);
        Assert.NotEqual(trigBuf, restBuf);
        Assert.Equal(4u, R32(Record + 0x08));         // restore 载荷 = 1 × popz = 4B
        Assert.Null(ApplyEngine.TryTakeReceipt());    // restore 的回执已随帧 1 批回执记 Ok
    }

    [Fact]
    public void Enqueue_WhilePreviousBatchPending_Rejected()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        NodeIndex.Build();
        Assert.Null(ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 2))));   // 批 7 入队未泵

        var second = Batch(PopzOp("entry_a", 2));
        second.BatchSeq = 8;
        var receipts = ApplyEngine.Enqueue(second);
        var r = Assert.Single(receipts!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("previous batch still pending", r.Reason);

        ApplyEngine.Pump();   // 只有批 7 被提交
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.Equal(7, receipt!.BatchSeq);
        Assert.Equal(8u, R32(Record + 0x08));
    }

    [Fact]
    public void Enqueue_AliasChildEntry_ResolveFail()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", startOff: 4);
        NodeIndex.Build();
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(PopzOp("entry_a_child", 1)))!);
        Assert.Equal("resolve", r.Stage);
        Assert.Contains("alias child entry", r.Reason);
    }

    /// <summary>#17 外扫实证：wrapper 根（槽/loader/函数声明裸根）无节点——索引只有子
    /// gml_Script_*（StartOff=4，record 覆盖整 buffer）。op.Entry=裸根名直名 miss → 回退
    /// 子名把住共享 buffer；子是 buffer 句柄不是 op 目标，alias-child 守卫只对直名命中生效
    /// （<see cref="Enqueue_AliasChildEntry_ResolveFail"/> 钉住直名命中子仍拒）。</summary>
    [Fact]
    public void Enqueue_RootHasNoNode_ResolvesViaChildEntry()
    {
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "gml_Script_msl_slot_0", startOff: 4);
        NodeIndex.Build();

        Assert.Null(ApplyEngine.Enqueue(Batch(PopzOp("msl_slot_0", 1))));   // 回退解析放行，非 alias-child 拒

        ApplyEngine.Pump();
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        Assert.Equal(4u, R32(AliasRecord + 0x08));                 // 1 × popz = 4B
        ulong ptr = R64(AliasRecord + 0x18);
        Assert.True(Mem.TestAllocs.ContainsKey(ptr));
        Assert.Equal(BcEncoder.Encode(PopzOp("msl_slot_0", 1).Instructions, new Translator(new OpMsg())),
            Mem.TestAllocs[ptr]);
    }

    /// <summary>#16b（#30 后仍真）：载荷局部数 &lt; 帧容量是安全的（帧偏大无害），旧相等
    /// 语义会误拒。#30 起容量已无上限语义，本用例退为一般放行回归。</summary>
    [Fact]
    public void Enqueue_PayloadFewerLocalsThanFrame_Passes()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 2);
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 1;
        Assert.Null(ApplyEngine.Enqueue(Batch(op)));
        ApplyEngine.Pump();
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
    }

    /// <summary>#16b + #30：frameOwner = 最小 StartOff 别名子（S2④：调子=从偏移 4 进入执行
    /// 子体——子才是帧主），不是根。旧「载荷 &gt; 子容量」拒批形态已随 #30 容量上限删除；
    /// 本测试改钉新语义下的 frameOwner 选择——父 locals=5 但子（帧主）locals=0，载荷带
    /// 局部 → 拒因取子的 0（若误取父的 5 则放行）。</summary>
    [Fact]
    public void Enqueue_PayloadLocalsButChildFrameZero_Rejected()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 5);
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", locals: 0, startOff: 4);
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 1;
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("boot frame locals", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));      // 未换
    }

    /// <summary>#16b：交换面完整性——全部别名（含子）的 node+0xA0 与各自 record+0x0C
    /// 必须镜像一致；容量检查通过（1 ≤ 子 1）但子镜像破裂 → 拒。</summary>
    [Fact]
    public void Enqueue_AliasMirrorMismatch_Rejected()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a", locals: 1);
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", locals: 1, startOff: 4);
        W32(AliasRecord + 0x0C, 3);   // 子记录镜像破裂（node+0xA0=1）
        NodeIndex.Build();
        var op = PopzOp("entry_a", 1);
        op.LocalsCount = 1;
        var r = Assert.Single(ApplyEngine.Enqueue(Batch(op))!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("locals mismatch", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));
    }

    // ---- #19 handler 表/pcmap 交换面（与 Trampoline 同根因同修复）----

    /// <summary>假通用槽：class → 0xA000+class（与 TableBuilderTests 同款）。</summary>
    static ulong FakeSlot(int cls) => 0xA000 + (ulong)cls;

    [Fact]
    public void Enqueue_Pump_WritesHandlerTableAndPcmap()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", startOff: 4);
        Assert.Equal(2, NodeIndex.Build());

        Assert.Null(ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 3)), FakeSlot));
        Assert.Equal(0UL, R64(Record + 0x20));          // Phase 1 绝不写任何记录字段

        ApplyEngine.Pump();

        // #19 全量交换面：+0x08/+0x18/+0x20/+0x28 四字段同换（缺表/pcmap = 旧特化产物
        // 把新 buffer 当操作数源静默空跑——trampoline 6000 帧零 entry 的同根因）
        var encoded = BcEncoder.Encode(PopzOp("entry_a", 3).Instructions, new Translator(new OpMsg()));
        var (table, map) = TableBuilder.Build(encoded, FakeSlot);
        foreach (var rec in new[] { Record, AliasRecord })
        {
            Assert.Equal(12u, R32(rec + 0x08));
            ulong tbl = R64(rec + 0x20), mp = R64(rec + 0x28);
            Assert.True(Mem.TestAllocs.ContainsKey(tbl));
            Assert.True(Mem.TestAllocs.ContainsKey(mp));
            Assert.Equal(table, Mem.TestAllocs[tbl]);
            Assert.Equal(map, Mem.TestAllocs[mp]);
        }
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
    }

    /// <summary>#19 真机自证（fail-closed）：旧 record 已特化（+0x20≠0）时按旧 buffer 重算
    /// 比对，不等即拒（validate 阶段）且一个都不换、什么都不分配。</summary>
    [Fact]
    public void Enqueue_OldTableMismatch_ValidateFail_NothingAllocated()
    {
        var live = new byte[8];   // 旧 buffer = 2 条零字指令（op 0，4B 步进）
        PlantNode(Node, Record, Name, Buf, "entry_a", live: live);
        var (table, map) = TableBuilder.Build(live, FakeSlot);
        var badTable = table.ToArray();
        badTable[0] ^= 0xFF;
        badTable.CopyTo(Mem.TestMap!, (int)(0x11800 - Base));
        map.CopyTo(Mem.TestMap!, (int)(0x11A00 - Base));
        W64(Record + 0x20, 0x11800);
        W64(Record + 0x28, 0x11A00);
        NodeIndex.Build();

        var r = Assert.Single(ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 1)), FakeSlot)!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("self-proof", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));          // 未换
        Assert.Equal(0x11800UL, R64(Record + 0x20));    // 旧表未动
        Assert.Empty(Mem.TestAllocs);                   // 自证先于任何分配
    }

    /// <summary>自证是逐别名记录的：任一共享记录的活表与重算不符 → 整批拒。</summary>
    [Fact]
    public void Enqueue_AliasOldTableMismatch_Rejected()
    {
        var live = new byte[8];
        PlantNode(Node, Record, Name, Buf, "entry_a", live: live);
        PlantNode(AliasNode, AliasRecord, AliasName, Buf, "entry_a_child", startOff: 4, live: live);
        var (table, map) = TableBuilder.Build(live, FakeSlot);
        table.CopyTo(Mem.TestMap!, (int)(0x11800 - Base));
        map.CopyTo(Mem.TestMap!, (int)(0x11A00 - Base));
        W64(Record + 0x20, 0x11800);                    // 根记录活表全等
        W64(Record + 0x28, 0x11A00);
        var badTable = table.ToArray();
        badTable[8] ^= 0xFF;
        badTable.CopyTo(Mem.TestMap!, (int)(0x11C00 - Base));
        map.CopyTo(Mem.TestMap!, (int)(0x11E00 - Base));
        W64(AliasRecord + 0x20, 0x11C00);               // 别名子活表破了
        W64(AliasRecord + 0x28, 0x11E00);
        NodeIndex.Build();

        var r = Assert.Single(ApplyEngine.Enqueue(Batch(PopzOp("entry_a", 1)), FakeSlot)!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("self-proof", r.Reason);
        Assert.Equal(Buf, R64(Record + 0x18));
        Assert.Equal(Buf, R64(AliasRecord + 0x18));
        Assert.Empty(Mem.TestAllocs);
    }

    // ---- #21 校准语料（CalibOps）：变量 id 唯一真源 = runner 已回填的活 buffer ----

    const ulong NodeB = 0x10300, RecordB = 0x10A00, NameB = 0x10E00, BufB = 0x11600;

    /// <summary>语料/换入共用指令流：[push.v spr, popz]（spr = vanilla 真实非内置实例变量名，
    /// VarIdSimulator 注释锚点 spr=1215）。</summary>
    static List<SemInstruction> PushSprSems() => new()
    {
        new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable, Inst = -1, Var = "spr", RefTop = 0xA0 },
        new() { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
    };

    /// <summary>#21 主回归：Enqueue 必须先对 CalibOps 收割再翻译——swap op 的 spr 操作数必须
    /// 等于语料 entry 活 buffer 里 runner 回填的真值（100000+4242），不是任何静态表的值。
    /// 修复前此测试红：CalibOps 被忽略 → 翻译未校准 → 整批拒绝。</summary>
    [Fact]
    public void Enqueue_CalibOps_HarvestLiveIds_BeforeTranslate()
    {
        // 语料 entry_a 的活 buffer：用校准表 4242 编码 = 模拟 runner 装载时的回填产物
        var sems = PushSprSems();
        byte[] liveA = BcEncoder.Encode(sems, new Translator(new OpMsg(),
            calibrated: new Dictionary<string, int> { ["i:spr"] = 4242 },
            registryIndexOf: _ => -1, scriptCodeId: _ => null));
        PlantNode(Node, Record, Name, Buf, "entry_a", live: liveA);
        PlantNode(NodeB, RecordB, NameB, BufB, "entry_b");
        Assert.Equal(2, NodeIndex.Build());

        var batch = new BatchMsg { BatchSeq = 9 };
        batch.CalibOps.Add(new OpMsg { Seq = -1, Kind = "calib", Entry = "entry_a", Instructions = sems });
        batch.Ops.Add(new OpMsg { Seq = 0, Kind = "swap", Entry = "entry_b", LocalsCount = 2, Instructions = PushSprSems() });

        Assert.Null(ApplyEngine.Enqueue(batch, FakeSlot));   // 收割命中 → 翻译通过 → 入队
        ApplyEngine.Pump();

        ulong ptr = R64(RecordB + 0x18);
        Assert.True(Mem.TestAllocs.ContainsKey(ptr));
        Assert.Equal(0xA0019732u, BitConverter.ToUInt32(Mem.TestAllocs[ptr], 4));   // 0xA0 | 104242
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
    }

    /// <summary>#21 语料解析补钉：gml_GlobalScript_ 根语料在 NodeIndex 里无直名节点（#17 实证：
    /// 运行时按绑定建节点，SCPT/FUNC 指向 gml_Script_ 裸名子）——收割必须剥前缀回退子名，
    /// 且子的 StartOff=4（≠0）也合法（只读不换：BufPtr=共享基址，根流含 4B 引导 B 从基址起对齐）。
    /// 修复前此测试红：回退拼成 gml_Script_gml_GlobalScript_scr_foo → node not found →
    /// 未收割 → swap op 未校准拒绝。</summary>
    [Fact]
    public void Enqueue_CalibOps_GlobalScriptRoot_ViaBareScriptChild()
    {
        // wrapper 根流形态：[B 引导 4B][push.v spr][popz] = 16B，与活 buffer 等长（Harvest 尾检）
        var rootSems = new List<SemInstruction>
        {
            new() { Kind = BcEncoder.OpB, Jump = 12 },
            new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable, Inst = -1, Var = "spr", RefTop = 0xA0 },
            new() { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
        };
        byte[] liveRoot = BcEncoder.Encode(rootSems, new Translator(new OpMsg(),
            calibrated: new Dictionary<string, int> { ["i:spr"] = 4242 },
            registryIndexOf: _ => -1, scriptCodeId: _ => null));
        // 活节点只有子名形态（StartOff=4，BufPtr=共享基址 = 根流起点）
        PlantNode(Node, Record, Name, Buf, "gml_Script_scr_foo", startOff: 4, live: liveRoot);
        PlantNode(NodeB, RecordB, NameB, BufB, "entry_b");
        Assert.Equal(2, NodeIndex.Build());

        var batch = new BatchMsg { BatchSeq = 9 };
        batch.CalibOps.Add(new OpMsg { Seq = -1, Kind = "calib", Entry = "gml_GlobalScript_scr_foo", Instructions = rootSems });
        batch.Ops.Add(new OpMsg { Seq = 0, Kind = "swap", Entry = "entry_b", LocalsCount = 2, Instructions = PushSprSems() });

        Assert.Null(ApplyEngine.Enqueue(batch, FakeSlot));   // 剥前缀回退命中 → 收割 → 翻译通过
        ApplyEngine.Pump();

        ulong ptr = R64(RecordB + 0x18);
        Assert.Equal(0xA0019732u, BitConverter.ToUInt32(Mem.TestAllocs[ptr], 4));   // 0xA0 | 104242
    }

    /// <summary>#22：改既有 vanilla 脚本的 swap 解析——op.Entry = gml_GlobalScript_ 根名
    /// （CodeDiffer 滤子条目后 ChangedEntry 就是根名，boot 已存在条目不改名），而运行时只有
    /// gml_Script_+裸名 子节点（#17：按绑定建节点，wrapper 根无节点）——Prepare 回退必须剥
    /// gml_GlobalScript_ 前缀，否则拼出 gml_Script_gml_GlobalScript_ 错名误拒「node not found」。
    /// 载荷 = 完整 wrapper 根流（[B 引导][body]——与 #16b 槽路径同构）。修复前此测试红。</summary>
    [Fact]
    public void Enqueue_SwapOp_GlobalScriptRoot_ViaBareScriptChild()
    {
        var rootSems = new List<SemInstruction>
        {
            new() { Kind = BcEncoder.OpB, Jump = 8 },          // wrapper 引导（4B）
            new() { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
            new() { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
        };
        // 活节点只有子名形态（StartOff=4，BufPtr=共享基址，record 覆盖整 buffer 12B）
        PlantNode(Node, Record, Name, Buf, "gml_Script_scr_foo", startOff: 4, live: new byte[12]);
        Assert.Equal(1, NodeIndex.Build());

        var batch = new BatchMsg { BatchSeq = 10 };
        batch.Ops.Add(new OpMsg { Seq = 0, Kind = "swap", Entry = "gml_GlobalScript_scr_foo", LocalsCount = 2, Instructions = rootSems });

        Assert.Null(ApplyEngine.Enqueue(batch, FakeSlot));   // 剥前缀回退命中子节点 → 入队
        ApplyEngine.Pump();

        ulong ptr = R64(Record + 0x18);
        Assert.True(Mem.TestAllocs.ContainsKey(ptr));        // 整 buffer 已换入
        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
    }

    /// <summary>#21 fail-closed 钉版：无 CalibOps 且未校准的变量 → validate 拒绝，整批不换。
    /// （修复前后皆绿——防未来有人把兜底加回来而不带任何拒绝语义。）</summary>
    [Fact]
    public void Enqueue_NoCalibOps_UncalibratedVar_Reject()
    {
        PlantNode(NodeB, RecordB, NameB, BufB, "entry_b");
        Assert.Equal(1, NodeIndex.Build());

        var batch = new BatchMsg { BatchSeq = 9 };
        batch.Ops.Add(new OpMsg { Seq = 0, Kind = "swap", Entry = "entry_b", LocalsCount = 2, Instructions = PushSprSems() });

        var r = Assert.Single(ApplyEngine.Enqueue(batch, FakeSlot)!);
        Assert.Equal("validate", r.Stage);
        Assert.Contains("not calibrated", r.Reason);
        Assert.Equal(BufB, R64(RecordB + 0x18));   // 未换
        Assert.Empty(Mem.TestAllocs);              // 未分配
    }
}
