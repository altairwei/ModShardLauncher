using System.Text;

namespace MslLive.Agent;

/// <summary>fix #34：运行时 STRG 追加——新字符串字面量热加（RE findings 2026-09-05）。
/// push.s 每次执行现查表（无特化物化）：表 A（全局槽 0x140815020 → u32 偏移数组，
/// chars = wadBase + 4 + off）、池 B（全局槽 0x140815120 = data.win 整体映射基址，
/// 运行时零拷贝）。追加 = 新块（游戏堆，每条 [u32 utf8len][chars+NUL pad4]，块地址须在
/// wadBase 的 u32 偏移域内）+ 新表 A'（旧表原样拷贝 + 新项）+ 原子换槽——读者仅 3 处
/// （push handler / 调试打印器 / 加载器）全走全局槽，RCU 语义下换指针安全。
///
/// 两阶段（与 ApplyEngine 对齐）：Assign（Prepare/编码期，pipe 线程）只记账不动内存；
/// Materialize（Enqueue 整批通过后）物化块与表并转正 committed；SwapTable（Pump，
/// 游戏线程 commit 窗）原子换槽——回执说成功时表必已就位。整批弃（Enqueue 拒批路径）
/// → DiscardPending，半物化的 id 绝不残留（否则去重表命中未写入的表项 → push 越界读）。
/// bootCount 自包含读取：count 在 STRG chunk 头+8 = 表指针−4。</summary>
public static class StrgAppendix
{
    public const ulong ProdTableSlotAddr = 0x140815020;   // 表 A 指针（push.s 解析链）
    public const ulong ProdPoolSlotAddr = 0x140815120;    // 池 B 指针（wad 映射基址）

    /// <summary>槽地址（生产常量 + 测试注入缝——与 AgentState.NodeSigFn 同款）。</summary>
    public static ulong TableSlotAddr { get; set; } = ProdTableSlotAddr;
    public static ulong PoolSlotAddr { get; set; } = ProdPoolSlotAddr;

    static readonly object gate = new();
    static readonly Dictionary<string, uint> committed = new();  // content → id（已物化）
    static readonly Dictionary<string, uint> pending = new();    // content → id（本批预分配）
    static readonly List<(string Content, uint Id)> pendingOrder = new();
    static long currentCount = -1;   // 当前表项数；-1 = 未读（首次 Assign 时 lazy 读）

    internal static void ResetForTest()
    {
        lock (gate)
        {
            committed.Clear(); pending.Clear(); pendingOrder.Clear();
            currentCount = -1;
        }
    }

    /// <summary>Prepare 期（BcEncoder → Translator.ResolveString）：StrgIndex=-1 的新字符串
    /// id 预分配。已物化复用同 id（跨批去重——表已含）；批内去重；否则顺延。只记账。</summary>
    public static uint Assign(string content)
    {
        lock (gate)
        {
            if (committed.TryGetValue(content, out uint id)) return id;
            if (pending.TryGetValue(content, out id)) return id;
            if (pendingOrder.Count >= 4096)
                throw new TranslationRejectException(
                    $"string appendix batch limit (4096) exceeded at '{content}'");
            if (currentCount < 0) currentCount = ReadBootCount();
            id = (uint)(currentCount + pendingOrder.Count);
            pending[content] = id;
            pendingOrder.Add((content, id));
            return id;
        }
    }

    /// <summary>Enqueue 整批通过后（pipe 线程）：物化新块与新表 A'，pending → committed。
    /// 无 pending → (0, 0)（不分配）。失败（分配失败 / 块在 u32 偏移域外 / 表结构异常）
    /// → TranslationRejectException 且 pending 全弃（调用方整批弃，下一批从当前表尾重新起）。
    /// 返回 (新表地址, 本批新增数)——换槽由 Pump 调 SwapTable 完成。</summary>
    public static (ulong NewTable, uint Added) Materialize()
    {
        List<(string Content, uint Id)> order;
        uint added;
        lock (gate)
        {
            if (pendingOrder.Count == 0) return (0, 0);
            order = new List<(string, uint)>(pendingOrder);
            added = (uint)order.Count;
        }

        ulong wadBase = Mem.ReadU64(PoolSlotAddr);
        ulong oldTable = Mem.ReadU64(TableSlotAddr);
        if (wadBase == 0 || oldTable == 0)
            return Fail($"wad/pool pointer null (wad 0x{wadBase:X}, table 0x{oldTable:X})");
        if (currentCount < 0) currentCount = ReadBootCount();
        long count = currentCount;

        // 新块：每条 [u32 utf8len][chars + NUL, 4 对齐]
        var rec = new byte[order.Sum(x => 4 + Pad4(Encoding.UTF8.GetByteCount(x.Content) + 1))];
        var offsets = new uint[order.Count];
        int off = 0;
        for (int i = 0; i < order.Count; i++)
        {
            offsets[i] = (uint)off;
            byte[] bytes = Encoding.UTF8.GetBytes(order[i].Content);
            BitConverter.TryWriteBytes(rec.AsSpan(off, 4), (uint)bytes.Length);
            bytes.CopyTo(rec, off + 4);
            // NUL + pad 已由 Pad4 预留且 rec 零初始化
            off += 4 + Pad4(bytes.Length + 1);
        }
        // 块必须在 wadBase 的 u32 偏移域内（push: chars = wadBase + 4 + u32 偏移）。
        // game alloc 是带 free-list 的堆（#27）：回收洞可能落在 wadBase 之下（E2E 实证
        // 0x2579560 < wadBase 0x258BDF0）——重试 + size 递增（大块把分配器逼出洞），
        // 全程记日志（分配器行为序列的第一手证据）；弃块不释放（与旧 buffer 同策）。
        ulong block = 0;
        var attempts = new List<string>();
        for (int grow = 0; grow < 4 && block == 0; grow++)
            for (int i = 0; i < 4 && block == 0; i++)
            {
                int size = rec.Length << grow;
                var padded = new byte[size];
                rec.CopyTo(padded, 0);
                ulong cand = Mem.AllocRW(padded);
                bool ok = cand != 0 && cand > wadBase && cand - wadBase < uint.MaxValue;
                attempts.Add($"0x{cand:X}{(ok ? "✓" : cand == 0 ? "(fail)" : "")}");
                if (ok) block = cand;
            }
        if (block == 0)
        {
            AgentState.Log($"strg appendix alloc attempts (wadBase 0x{wadBase:X}): {string.Join(" ", attempts)}");
            return Fail($"string block allocation: no candidate inside u32 window of wad base 0x{wadBase:X} " +
                        $"(attempts: {string.Join(" ", attempts.Take(8))})");
        }
        if (attempts.Count > 1)
            AgentState.Log($"strg appendix block 0x{block:X} after {attempts.Count} attempts " +
                           $"(wadBase 0x{wadBase:X}): {string.Join(" ", attempts)}");

        // 新表 A'：旧表原样 + 新项（记录起点 − wadBase）
        byte[] oldOffsets = Mem.ReadBytes(oldTable, (int)count * 4);
        if (oldOffsets.Length != (int)count * 4)
            return Fail($"old offset table unreadable ({count} entries @0x{oldTable:X})");
        var table = new byte[(count + order.Count) * 4];
        Buffer.BlockCopy(oldOffsets, 0, table, 0, oldOffsets.Length);
        for (int i = 0; i < order.Count; i++)
            BitConverter.TryWriteBytes(table.AsSpan((int)(count + i) * 4, 4),
                (uint)(block + offsets[i] - wadBase));
        ulong newTable = Mem.AllocRW(table);
        if (newTable == 0) return Fail("string offset table allocation failed");

        lock (gate)
        {
            foreach (var (content, id) in order) committed[content] = id;
            pending.Clear(); pendingOrder.Clear();
            currentCount = count + order.Count;
        }
        AgentState.Log($"strg appendix: {order.Count} strings appended, ids {order[0].Id}..{order[^1].Id}, " +
                       $"block 0x{block:X}, table 0x{newTable:X} (swap at commit)");
        return (newTable, added);

        (ulong, uint) Fail(string why)
        {
            DiscardPending();
            throw new TranslationRejectException($"string appendix: {why}");
        }
    }

    /// <summary>Pump（游戏线程 commit 窗）原子换槽——新表已完整就位，64 位对齐写。</summary>
    public static void SwapTable(ulong newTable) =>
        Mem.WriteU64(TableSlotAddr, newTable);

    /// <summary>Enqueue 拒批路径：丢弃本批预分配（未物化的 id 不得残留去重表）。</summary>
    public static void DiscardPending()
    {
        lock (gate) { pending.Clear(); pendingOrder.Clear(); }
    }

    /// <summary>boot STRG count：在 STRG chunk 头+8 = 表指针−4（加载器钉版：
    /// 表 A = chunk 头 + 0xC = tag/size/count 之后）。</summary>
    static long ReadBootCount()
    {
        ulong table = Mem.ReadU64(TableSlotAddr);
        if (table == 0)
            throw new TranslationRejectException("string appendix: STRG table pointer null (wad not loaded?)");
        uint count = Mem.ReadU32(table - 4);
        if (count == 0 || count > 100_000_000)
            throw new TranslationRejectException($"string appendix: implausible STRG count {count}");
        return count;
    }

    static int Pad4(int n) => (n + 3) & ~3;
}
