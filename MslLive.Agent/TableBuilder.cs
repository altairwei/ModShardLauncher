namespace MslLive.Agent;

/// <summary>EnsureSpecialized（exe 0x140297B80/0x140297C99，#19 离线解剖钉版）的 agent 侧复刻：
/// 为换入 buffer 构建派发 handler 表（record+0x20）与 pcmap（record+0x28）。
/// GMS2.3 VM 是线程化代码执行——主循环按 ctx+0x90 指令序号从 record+0x20 表预取 handler，
/// buffer 仅供操作数；表在首执行时按 buffer 内容懒构建、同 buffer 记录共享。因此换 buffer
/// 必须连 +0x20/+0x28 一起换，否则旧表把新字节当操作数源静默空跑（#19 根因）。
/// 规则真源 = 反汇编：
///   class = opcode &amp; 0x1F；
///   class 0x19 ∧ T1==2 → 读 4B 操作数分派：&lt;100000 → VaNativeCall；
///     ∈[100000,500000]（(uint)(op-100000) ≤ 0x61A80）且 ≠0x7A11F → VaScriptCall；否则回通用槽；
///   class 0x07 ∧ (w&gt;&gt;8)&amp;0xF==0 ∧ b2==0x52 → VaConvIV；
///   class 0x05 ∧ T1==5 ∧ T2==5 ∧ low16==0xFFF9 ∧ bit30 → VaPopLocalSpecial；
///   其余 → 通用槽（生产侧运行时读 GenericSlotTableVa+class*8，测试注入假槽）。
/// 长度：bit30（0x40000000）= 扩展位；扩展 len = LengthTable[T1] + 4，否则 4
/// （与 BcEncoder.ByteSize 同知识双源——改一边必须查另一边）。
/// pcmap：起始单元写指令序号、内部单元 0xFFFFFFFF（pass memset 0xFF 后只写起点）、+1 零哨兵；
/// 表 = count+1 qword，末位零哨兵（pass calloc 语义）。</summary>
public static class TableBuilder
{
    // 三个专用 handler + 通用槽表 VA（文件 VA==运行时 VA，S1 实证无 ASLR）。
    // const（非 AgentState 式 static 域）：这些是 exe 静态地址，测试 [InlineData] 需要编译期常量。
    public const ulong VaNativeCall = 0x14028B910;      // 原生立即数派发器（操作数 <100000；无边界检查）
    public const ulong VaScriptCall = 0x14028BA30;      // 脚本调用 wrapper（100000+codeId 空间）
    public const ulong VaConvIV = 0x140282C80;          // conv.i.v 专用
    public const ulong VaPopLocalSpecial = 0x140288160; // pop local 专用
    public const ulong GenericSlotTableVa = 0x1406314F0; // 32 qword 通用槽表（.rdata）

    /// <summary>生产侧槽解析器：运行时读游戏通用槽表。</summary>
    public static ulong RuntimeSlot(int cls) => Mem.ReadU64(GenericSlotTableVa + (ulong)cls * 8);

    /// <summary>长度表（imageBase+0x6314B0 实测值）：扩展指令 len = 表[T1] + 4。</summary>
    static readonly byte[] LengthTable = { 8, 4, 4, 8, 4, 4, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>对 code 建 handler 表与 pcmap。buffer 畸形（未 4 对齐/空/指令越界/立即数截断）
    /// → TranslationRejectException（fail-closed，与翻译器同族）。</summary>
    public static (byte[] Table, byte[] PcMap) Build(byte[] code, Func<int, ulong> slot)
    {
        if (code.Length == 0 || (code.Length & 3) != 0)
            throw new TranslationRejectException($"table build: buffer {code.Length}B empty or not 4-aligned");

        int units = code.Length / 4;
        var handlers = new List<ulong>();
        var map = new uint[units + 1];
        for (int i = 0; i < units; i++) map[i] = 0xFFFFFFFF;   // memset 0xFF：内部单元保持 -1
        map[units] = 0;                                        // +1 零哨兵

        int pc = 0;   // 单元游标（4B/单元）
        while (pc < units)
        {
            uint w = BitConverter.ToUInt32(code, pc * 4);
            int len = LengthOf(w);
            if (pc + len / 4 > units)
                throw new TranslationRejectException(
                    $"table build: instruction @unit {pc} (word 0x{w:X8}) len {len} overruns {units} units");

            map[pc] = (uint)handlers.Count;
            handlers.Add(HandlerFor(w, pc, units, code, slot));
            pc += len / 4;
        }
        handlers.Add(0);   // +1 零哨兵

        var table = new byte[handlers.Count * 8];
        for (int i = 0; i < handlers.Count; i++)
            BitConverter.GetBytes(handlers[i]).CopyTo(table, i * 8);
        var pcmap = new byte[map.Length * 4];
        Buffer.BlockCopy(map, 0, pcmap, 0, pcmap.Length);
        return (table, pcmap);
    }

    static int LengthOf(uint w) =>
        (w & 0x40000000) == 0 ? 4 : LengthTable[(w >> 16) & 0xF] + 4;

    /// <summary>#19 真机自证（fail-closed）：旧 record 已特化（+0x20≠0）时，对旧 buffer 重算
    /// 表/pcmap 并与活表逐字节比对——不等 = 特化规则复刻错了，装上就是野派发，调用方必须拒装。
    /// +0x20==0（从未执行、从未特化）→ 跳过：首执行会按新 buffer 建表（spike 当年的偶然形态）。
    /// +0x20≠0 而 +0x28==0 = 模型外形态（pass 同时分配两者）→ 同样拒。</summary>
    public static bool SelfProofRecord(ulong record, Func<int, ulong> slot, out string why)
    {
        why = "";
        ulong oldTable = Mem.ReadU64(record + 0x20);
        if (oldTable == 0) return true;
        ulong oldBuf = Mem.ReadU64(record + 0x18);
        int oldLen = (int)Mem.ReadU32(record + 0x08);
        byte[] oldBytes = Mem.ReadBytes(oldBuf, oldLen);
        if (oldLen <= 0 || oldBytes.Length != oldLen)
        { why = $"old buffer unreadable ({oldLen}B @0x{oldBuf:X})"; return false; }
        byte[] expTable, expMap;
        try { (expTable, expMap) = Build(oldBytes, slot); }
        catch (TranslationRejectException ex) { why = $"old buffer rebuild rejected: {ex.Message}"; return false; }
        if (!Mem.ReadBytes(oldTable, expTable.Length).SequenceEqual(expTable))
        { why = $"old handler table @0x{oldTable:X} != rebuild from old buffer"; return false; }
        ulong oldMap = Mem.ReadU64(record + 0x28);
        if (oldMap == 0)
        { why = "handler table present but pcmap missing (model violation)"; return false; }
        if (!Mem.ReadBytes(oldMap, expMap.Length).SequenceEqual(expMap))
        { why = $"old pcmap @0x{oldMap:X} != rebuild from old buffer"; return false; }
        return true;
    }

    /// <summary>交换面四字段写（#19 全量交换面）：+0x08 长度 → +0x18 buffer → +0x28 pcmap →
    /// +0x20 handler 表（一致性点，最后写）。反序会开「旧 buffer+新表」窗口 = 野调用崩溃；
    /// 本序的最坏窗口 = 旧表+新 buffer = #19 已实证的静默 no-op（无害）。</summary>
    public static void WriteRecord(ulong record, uint newLen, ulong newBuf, ulong newTable, ulong newMap)
    {
        Mem.WriteU32(record + 0x08, newLen);
        Mem.WriteU64(record + 0x18, newBuf);
        Mem.WriteU64(record + 0x28, newMap);
        Mem.WriteU64(record + 0x20, newTable);
    }

    static ulong HandlerFor(uint w, int pc, int units, byte[] code, Func<int, ulong> slot)
    {
        int cls = (int)((w >> 24) & 0x1F);
        int t1 = (int)((w >> 16) & 0xF);

        // 立即数调用臂：pass 无条件读 [buffer+pc*4+4] 作操作数
        if (cls == 0x19 && t1 == 2)
        {
            if (pc + 1 >= units)
                throw new TranslationRejectException($"table build: call immediate @unit {pc} truncated operand");
            uint op = BitConverter.ToUInt32(code, pc * 4 + 4);
            if (op < 100000) return VaNativeCall;
            if (op - 100000 <= 0x61A80 && op != 0x7A11F) return VaScriptCall;
            return slot(cls);
        }
        // conv.i.v 专用（b2 = 指令字第 2 字节）
        if (cls == 0x07 && ((w >> 8) & 0xF) == 0 && ((w >> 16) & 0xFF) == 0x52)
            return VaConvIV;
        // pop local 专用
        if (cls == 0x05 && t1 == 5 && ((w >> 20) & 0xF) == 5
            && (w & 0xFFFF) == 0xFFF9 && (w & 0x40000000) != 0)
            return VaPopLocalSpecial;
        return slot(cls);
    }
}
