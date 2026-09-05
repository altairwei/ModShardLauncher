using System.Text;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>fix #34：运行时 STRG 追加（RE findings 2026-09-05 §一/§二/§四）。
/// push.s 每次现查表——表 A（槽 0x140815020 → u32 偏移数组，chars = wadBase+4+off）、
/// 池 B（槽 0x140815120 = wad 映射基址，零拷贝，追加不碰）；追加 = 新块（游戏堆，
/// [u32 len][utf8+NUL pad4]，偏移域 u32 内）+ 新表（旧表拷贝+新项）+ 原子换槽。
/// 地址布局：TestBase 0x7EFF_8000_0000（Mem 假分配 0x7F00_0000_0000 起——块−wadBase
/// ≈ 2GB 天然落在 u32 偏移域内；越域拒收用例用假 wadBase 构造）。</summary>
public class StrgAppendixTests : IDisposable
{
    const ulong SIG = 0x1406BE508;
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x7EFF_8000_0000;

    // 槽与结构（生产常量可注入——与 AgentState.NodeSigFn 同款测试缝）
    const ulong TableSlot = Base + 0x800, PoolSlot = Base + 0x808;
    const ulong WadBase = Base + 0x1000;       // 池 B（wad 映射基址，仅作值用）
    const ulong OldTable = Base + 0x2000;      // 旧表 A（wad 内 offsets 数组）
    // 集成用例的节点面（与 ApplyEngineTests 同款 Plant，地址挪到本类 TestMap 域）
    const ulong Node = Base + 0x3000, Record = Base + 0x3800, Name = Base + 0x3C00, Buf = Base + 0x4000;

    public StrgAppendixTests()
    {
        Mem.TestMap = new byte[0x8000];
        Mem.TestBase = Base;
        Mem.TestAllocs.Clear();
        AgentState.ResetForTest();
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
        StrgAppendix.TableSlotAddr = TableSlot;
        StrgAppendix.PoolSlotAddr = PoolSlot;
        StrgAppendix.ResetForTest();
        PlantStrg(3, 0x100, 0x110, 0x120);   // boot 表：3 条假偏移
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        Mem.TestAllocs.Clear();
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
        AgentState.ResetForTest();
        StrgAppendix.ResetForTest();
        StrgAppendix.TableSlotAddr = StrgAppendix.ProdTableSlotAddr;
        StrgAppendix.PoolSlotAddr = StrgAppendix.ProdPoolSlotAddr;
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static uint R32(ulong addr) => BitConverter.ToUInt32(Mem.TestMap!, (int)(addr - Base));
    static ulong R64(ulong addr) => BitConverter.ToUInt64(Mem.TestMap!, (int)(addr - Base));

    static void PlantStrg(uint count, params uint[] offsets)
    {
        W64(PoolSlot, WadBase);
        W64(TableSlot, OldTable);
        W32(OldTable - 4, count);                    // count @ 表-4（STRG chunk 头+8）
        for (int i = 0; i < offsets.Length; i++)
            W32(OldTable + (ulong)i * 4, offsets[i]);
    }

    static void PlantNode(ulong node, ulong record, ulong nameAt, ulong bufAt, string name, uint locals = 2)
    {
        W64(node, SIG);
        W32(node + 0x64, 0x00FFFFFF);
        W64(node + 0x68, record);
        W64(record, EXEC);
        W32(record + 0x08, 8);
        W32(record + 0x0C, locals);
        W64(record + 0x18, bufAt);
        W64(node + 0x80, nameAt);
        Encoding.ASCII.GetBytes(name).CopyTo(Mem.TestMap!, (int)(nameAt - Base));
        W32(node + 0x88, 1234);
        W32(node + 0xA0, locals);
        new byte[8].CopyTo(Mem.TestMap!, (int)(bufAt - Base));
    }

    [Fact]
    public void Assign_SequencesAndDedupsInBatch()
    {
        Assert.Equal(3u, StrgAppendix.Assign("alpha"));    // boot count=3 → 首个新 id=3
        Assert.Equal(4u, StrgAppendix.Assign("beta"));
        Assert.Equal(3u, StrgAppendix.Assign("alpha"));    // 批内去重
    }

    [Fact]
    public void Assign_ReusesCommittedAcrossBatches()
    {
        StrgAppendix.Assign("alpha");
        var (table, _) = StrgAppendix.Materialize();
        Assert.NotEqual(0u, table);
        StrgAppendix.SwapTable(table);
        // 第二批：已 commit 的 content 复用同 id（表已含——不重复追加）
        Assert.Equal(3u, StrgAppendix.Assign("alpha"));
        var (t2, added2) = StrgAppendix.Materialize();
        Assert.Equal(0u, added2);                          // 无新增
        Assert.Equal(0u, t2);                              // 无 pending → 不建新表
        // 新 content 继续顺延
        Assert.Equal(4u, StrgAppendix.Assign("gamma"));
    }

    [Fact]
    public void Assign_RejectsNullTable()
    {
        W64(TableSlot, 0);
        Assert.Throws<TranslationRejectException>(() => StrgAppendix.Assign("alpha"));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(500_000_000u)]
    public void Assign_RejectsImplausibleCount(uint bad)
    {
        W32(OldTable - 4, bad);
        Assert.Throws<TranslationRejectException>(() => StrgAppendix.Assign("alpha"));
    }

    [Fact]
    public void Materialize_BuildsTableAndBlock()
    {
        StrgAppendix.Assign("AB");        // id 3：len 2，utf8 2B + NUL = 3 → pad 1 → 记录 4+4=8
        StrgAppendix.Assign("ΓΔ");        // id 4：utf8 4B + NUL = 5 → pad 3 → 记录 4+8=12
        var (newTable, added) = StrgAppendix.Materialize();
        Assert.Equal(2u, added);

        // 新表在假分配域（TestAllocs），不在 TestMap——从字典读
        var t = Mem.TestAllocs[newTable];
        static uint T32(byte[] a, int i) => BitConverter.ToUInt32(a, i);
        // 新表：旧 3 项原样 + 2 新项（记录起点 − wadBase，u32）
        Assert.Equal(0x100u, T32(t, 0));
        Assert.Equal(0x110u, T32(t, 4));
        Assert.Equal(0x120u, T32(t, 8));
        // 新块 = Materialize 的另一次假分配（先块后表——块地址 = 表地址 − 0x1000）
        ulong block = newTable - 0x1000;
        var bytes = Mem.TestAllocs[block];
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 0));       // len = utf8 字节数（不含 NUL）
        Assert.Equal("AB\0", Encoding.UTF8.GetString(bytes, 4, 3));
        Assert.Equal(4u, BitConverter.ToUInt32(bytes, 8));       // "ΓΔ" utf8 = 4B
        Assert.Equal("ΓΔ\0", Encoding.UTF8.GetString(bytes, 12, 5));
        // 新表项 = 各记录起点 − WadBase
        Assert.Equal((uint)(block - WadBase), T32(t, 12));
        Assert.Equal((uint)(block + 8 - WadBase), T32(t, 16));
    }

    [Fact]
    public void Materialize_RejectsBlockOutsideWindow()
    {
        // 假 wadBase 高于假分配域 → 块地址 < wadBase，u32 偏移不可表达 → fail-closed
        W64(PoolSlot, 0x7FFF_0000_0000);
        StrgAppendix.Assign("alpha");
        Assert.Throws<TranslationRejectException>(() => StrgAppendix.Materialize());
        // 拒后 pending 已弃：下批同 content 重新分配（不残留半物化 id）
        W64(PoolSlot, WadBase);
        Assert.Equal(3u, StrgAppendix.Assign("alpha"));
    }

    [Fact]
    public void DiscardPending_NextBatchGetsFreshIds()
    {
        StrgAppendix.Assign("alpha");
        StrgAppendix.DiscardPending();       // 模拟 Enqueue 整批弃
        Assert.Equal(3u, StrgAppendix.Assign("alpha"));   // 同 id 重发（未被污染）
        var (t, added) = StrgAppendix.Materialize();
        Assert.Equal(1u, added);
    }

    [Fact]
    public void SwapTable_WritesSlotAtomically()
    {
        StrgAppendix.Assign("alpha");
        var (table, _) = StrgAppendix.Materialize();
        StrgAppendix.SwapTable(table);
        Assert.Equal(table, R64(TableSlot));
    }

    // ---- ApplyEngine 集成（两阶段语义：Enqueue 物化、Pump 换槽、回执明细）----

    static OpMsg StringOp(string entry, string content) => new()
    {
        Seq = 0, Kind = "swap", Entry = entry, LocalsCount = 2,
        Instructions = new List<SemInstruction>
        {
            new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TString, Str = content },
            new() { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
        },
        Strings = { new StrRef { Content = content, StrgIndex = -1 } },
    };

    [Fact]
    public void Enqueue_FreshString_AppendsAtPumpAndReports()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        Assert.Equal(1, NodeIndex.Build());
        var op = StringOp("entry_a", "fix34_new_string");
        var fresh = Mem.TestAllocs.Count;    // 分配计数基线

        Assert.Null(ApplyEngine.Enqueue(new BatchMsg { BatchSeq = 7, Ops = { op } }));
        // 分配计数：Prepare 的 buffer/table/pcmap + Materialize 的块/表 A' = +5
        Assert.Equal(fresh + 5, Mem.TestAllocs.Count);

        // Phase 1 绝不换槽（旧表指针未动）
        Assert.Equal(OldTable, R64(TableSlot));

        ApplyEngine.Pump();
        // Pump 换槽：新表就位（≠ 旧表）——新表在假分配域，从 TestAllocs 读
        ulong swapped = R64(TableSlot);
        Assert.NotEqual(OldTable, swapped);
        var t = Mem.TestAllocs[swapped];
        Assert.Equal(0x100u, BitConverter.ToUInt32(t, 0));       // 旧表首项原样
        // 新表末项 = 新字符串记录偏移（块在 wadBase 的 u32 域内，id = bootCount(3) → 表项 3）
        uint off = BitConverter.ToUInt32(t, 3 * 4);
        Assert.True(off > 0);
        // 换入 buffer 已生效
        Assert.NotEqual(Buf, R64(Record + 0x18));

        var receipt = ApplyEngine.TryTakeReceipt();
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        var r = Assert.Single(receipt.Ops);
        Assert.True(r.Ok);
        Assert.Contains("fix34_new_string", Assert.Single(r.StrgAppended));
    }

    [Fact]
    public void Enqueue_RejectedBatch_DoesNotPolluteAppendix()
    {
        PlantNode(Node, Record, Name, Buf, "entry_a");
        Assert.Equal(1, NodeIndex.Build());
        // node not found 的第二个 op → 整批弃（D4）
        var good = StringOp("entry_a", "fix34_x");
        var bad = StringOp("entry_missing", "fix34_y");

        var receipts = ApplyEngine.Enqueue(new BatchMsg { BatchSeq = 7, Ops = { good, bad } });
        Assert.NotNull(receipts);
        Assert.Equal(2, receipts!.Count);

        // 槽未换 + 附录未污染：下一批同 content 从 boot count 重新起
        Assert.Equal(OldTable, R64(TableSlot));
        Assert.Equal(3u, StrgAppendix.Assign("fix34_x"));
    }
}
