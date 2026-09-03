using System.Runtime.InteropServices;
using System.Text;

namespace MslLive.Agent;

/// <summary>进程内直读（不走红 ReadProcessMemory）。所有读带 VirtualQuery 守卫：
/// 未提交/不可读的地址返回 0 / 空串而不是访问违例——注入组件的首要纪律是绝不崩游戏。
/// 测试经 TestMap/TestBase 注入合成内存（非空时 Read* 与 ScanQword 全部走数组）。</summary>
public static unsafe class Mem
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemInfo
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, _align1;
        public ulong RegionSize;
        public uint State, Protect, Type, _align2;
    }

    [DllImport("kernel32.dll")] static extern nuint VirtualQuery(ulong addr, out MemInfo info, nuint len);
    [DllImport("kernel32.dll")] static extern ulong VirtualAlloc(ulong addr, nuint size, uint type, uint protect);

    const uint MEM_COMMIT = 0x1000;
    const uint MEM_PRIVATE = 0x20000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;
    // sizeof(MemInfo) = 48（8+8+4+4+8+4+4+4+4）；裸 sizeof(自定义struct) 要 unsafe，
    // 而 unsafe 迭代器要 C#13——用常量绕开（布局有 LayoutKind.Sequential 背书）。
    const nuint MbiSize = 48;

    internal static byte[]? TestMap;
    internal static ulong TestBase;
    /// <summary>测试缝（fix-loop #24）：非空时替代「整张 TestMap 一个区段」——注入
    /// 多区段形态（TestMap 仍是内容后端，区段是它的子区间，基址可高于 TestBase）。</summary>
    internal static List<(ulong Base, ulong Size)>? TestRegions;

    /// <summary>MEM_COMMIT ∧ MEM_PRIVATE ∧ (READWRITE|EXECUTE_READWRITE) ∧ !GUARD 的区域枚举。</summary>
    public static IEnumerable<(ulong Base, ulong Size)> Regions()
    {
        if (TestMap != null)
        {
            if (TestRegions != null) { foreach (var r in TestRegions) yield return r; }
            else yield return (TestBase, (ulong)TestMap.LongLength);
            yield break;
        }
        ulong addr = 0;
        while (VirtualQuery(addr, out var m, MbiSize) != 0)
        {
            ulong next = m.BaseAddress + m.RegionSize;
            if (m.State == MEM_COMMIT && m.Type == MEM_PRIVATE &&
                ((m.Protect & 0xFF) is 0x04 or 0x40) && (m.Protect & PAGE_GUARD) == 0)
                yield return (m.BaseAddress, m.RegionSize);
            if (next <= addr) yield break;   // 环绕/异常区域，防御
            addr = next;
        }
    }

    /// <summary>单区段 qword 扫描（hits=null 时只计数——探针模式免命中列表开销）。
    /// TestMap 模式按绝对地址索引（区段基址可高于 TestBase——TestRegions 缝）。</summary>
    static int ScanRange(ulong b, ulong size, ulong value, List<ulong>? hits)
    {
        int count = 0;
        if (TestMap != null)   // 合成内存：扫数组，别把假基址当真指针解引用
        {
            long n = TestMap.LongLength;
            long start = (long)(b - TestBase);
            long end = start + (long)size;
            if (start < 0) start = 0;
            if (end > n) end = n;   // 防御：缝配置越出数组尾（错配测试不该崩）
            for (long i = start; i + 8 <= end; i += 8)
                if (BitConverter.ToUInt64(TestMap, (int)i) == value)
                {
                    count++;
                    hits?.Add(TestBase + (ulong)i);
                }
            return count;
        }
        byte* p = (byte*)b;
        for (ulong off = 0; off + 8 <= size; off += 8)
            if (*(ulong*)(p + off) == value)
            {
                count++;
                hits?.Add(b + off);
            }
        return count;
    }

    /// <summary>8 对齐 qword 全量值扫描（NodeIndex 全扫兜底 / 测试直扫）。
    /// 非常规返回 List 而非迭代器：unsafe 指针在迭代器方法里要 C#13，net6 默认 C#10。</summary>
    public static List<ulong> ScanQword(ulong value)
    {
        var hits = new List<ulong>();
        foreach (var (b, size) in Regions())
            ScanRange(b, size, value, hits);
        return hits;
    }

    /// <summary>头窗：#11 离线 dump 实证节点专属区段 SIG 集中在头 0x100–0x400 →
    /// 头 16KB 必含 SIG。</summary>
    public const ulong ProbeWindow = 0x4000;

    /// <summary>网格窗间距/宽度（fix-loop #24 v2）：每 64KB 一个 1KB 窗，扫区段内部。
    /// 头窗抓「节点专属区段」；网格窗抓「尾溢区段」——专属区段装满后尾批节点溢进
    /// 与其他堆数据共享的区段深处（13:54 boot 真机外扫：+0x53E00/+0xE8A00 两个区段、
    /// 766 原始节点/378 去重名 = gml_RoomCC_*_Create 全漏）。保证：节点 run
    /// ≥ GridSpacing+GridWindow 必含一个 64K 整倍数点（鸽笼），其窗落在 run 内且宽于
    /// 节点间距（实测 ~176–257B）→ 必含一个节点头，与 run 起始对齐无关。窗页对齐 →
    /// 每窗单页成本（2081MB/64K ≈ 33K 页 ≈ 133MB 触摸）。残留：<64KB 微型尾溢仍可能
    /// 漏（精确解 = MSL 下发期望去重名数对账，方案 v2b 另议）。</summary>
    public const ulong GridSpacing = 0x10000;
    public const ulong GridWindow = 0x400;

    /// <summary>最近一次 ScanQwordProbed 的区段统计（Probed 探测/Hitted 命中）——
    /// agent.log 的门控证据源（真机预期 ~19 命中 / 数百探测）。</summary>
    internal static (int Probed, int Hitted) LastProbe;

    /// <summary>fix-loop #24 probe 门控扫描：每区段先探针（头窗 + 网格窗），含 value
    /// 才全扫该区段。22min 索引的真根因不是扫描带宽——全量扫描把游戏 3.3GB 工作集
    /// 逐出，之后 38K 命中 × ~7 次散读全吃 ~4ms 硬页错误（#9 真机实测 26min43s）。
    /// 门控把扫描量 3.5GB → ~20MB，工作集基本不动。探针漏区（SIG 深埋且无 ≥64KB
    /// run 的区段）由 NodeIndex 平台期全扫兜底。</summary>
    public static List<ulong> ScanQwordProbed(ulong value)
    {
        var hits = new List<ulong>();
        int probed = 0, hitted = 0;
        foreach (var (b, size) in Regions())
        {
            probed++;
            if (!ProbeHits(b, size, value)) continue;
            hitted++;
            ScanRange(b, size, value, hits);
        }
        LastProbe = (probed, hitted);
        return hits;
    }

    /// <summary>区段探针 = 头 ProbeWindow 窗 + 每 GridSpacing 一个 GridWindow 窗。</summary>
    static bool ProbeHits(ulong b, ulong size, ulong value)
    {
        ulong head = size < ProbeWindow ? size : ProbeWindow;
        if (ScanRange(b, head, value, null) > 0) return true;
        for (ulong g = GridSpacing; g < size; g += GridSpacing)
        {
            ulong w = GridWindow < size - g ? GridWindow : size - g;
            if (ScanRange(b + g, w, value, null) > 0) return true;
        }
        return false;
    }

    /// <summary>memchr 式 AOB 扫描（byte? 通配 null；Task 14 AOB 自证用）。</summary>
    public static List<ulong> ScanAob(byte?[] pattern)
    {
        var hits = new List<ulong>();
        if (pattern.Length == 0) return hits;
        foreach (var (b, size) in Regions())
        {
            if (TestMap != null)
            {
                long n = TestMap.LongLength;
                long start = (long)(b - TestBase);
                long end = start + (long)size;
                if (start < 0) start = 0;
                if (end > n) end = n;   // TestRegions 缝下按绝对地址索引（同 ScanRange）
                for (long i = start; i + pattern.Length <= end; i++)
                {
                    int k = 0;
                    while (k < pattern.Length && (pattern[k] == null || TestMap[i + k] == pattern[k]!.Value)) k++;
                    if (k == pattern.Length) hits.Add(TestBase + (ulong)i);
                }
                continue;
            }
            byte* p = (byte*)b;
            int np = pattern.Length;
            for (ulong off = 0; off + (ulong)np <= size; off++)
            {
                int i = 0;
                while (i < np && (pattern[i] == null || p[off + (ulong)i] == pattern[i]!.Value)) i++;
                if (i == np) hits.Add(b + off);
            }
        }
        return hits;
    }

    static bool Readable(ulong addr, int len)
    {
        if (TestMap != null)
            return addr >= TestBase && addr - TestBase + (ulong)len <= (ulong)TestMap.LongLength;
        return VirtualQuery(addr, out var m, (nuint)sizeof(MemInfo)) != 0
            && m.State == MEM_COMMIT && addr >= m.BaseAddress
            && addr - m.BaseAddress + (ulong)len <= m.RegionSize
            && (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
    }

    public static ulong ReadU64(ulong addr)
    {
        if (TestMap != null)
            return Readable(addr, 8) ? BitConverter.ToUInt64(TestMap, (int)(addr - TestBase)) : 0;
        return Readable(addr, 8) ? *(ulong*)addr : 0;
    }

    public static uint ReadU32(ulong addr)
    {
        if (TestMap != null)
            return Readable(addr, 4) ? BitConverter.ToUInt32(TestMap, (int)(addr - TestBase)) : 0;
        return Readable(addr, 4) ? *(uint*)addr : 0;
    }

    /// <summary>读 NUL 结尾字符串；max 内无 NUL 则取满 max（注册表内联名恰占满 0x40 的实测形态）。
    /// 不可读 → ""。fix-loop #24：原实现每字节一次 Readable（=一次 VirtualQuery 系统调用）——
    /// 34,724 节点 × ~30 字符名是百万次级调用；改为一次查界（可读区段尾 / addr+max 钳制）
    /// 后连续读到 NUL / 界为止，语义与旧「首不可读字节停」同界。</summary>
    public static string ReadCString(ulong addr, int max)
    {
        if (addr == 0) return "";
        ulong limit;   // 独占上界
        if (TestMap != null)
        {
            if (addr < TestBase) return "";
            limit = TestBase + (ulong)TestMap.LongLength;
        }
        else
        {
            if (VirtualQuery(addr, out var m, MbiSize) == 0 || m.State != MEM_COMMIT ||
                (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return "";
            limit = m.BaseAddress + m.RegionSize;
        }
        ulong capped = addr + (ulong)max;
        if (capped < limit) limit = capped;
        var buf = new byte[max];
        int n = 0;
        for (ulong a = addr; a < limit && n < max; a++, n++)
        {
            byte c = TestMap != null ? TestMap[(int)(a - TestBase)] : *(byte*)a;
            if (c == 0) break;
            buf[n] = c;
        }
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    /// <summary>整块读（proof 读回活 buffer 用）。守卫失败 → 空数组（fail-closed：调用方按长度不符处理）。</summary>
    public static byte[] ReadBytes(ulong addr, int len)
    {
        if (len <= 0 || addr == 0) return Array.Empty<byte>();
        if (!Readable(addr, len)) return Array.Empty<byte>();
        var buf = new byte[len];
        if (TestMap != null) Array.Copy(TestMap, (int)(addr - TestBase), buf, 0, len);
        else Marshal.Copy((nint)addr, buf, 0, len);
        return buf;
    }

    // ---- 写原语（Task 14 trampoline / Task 15 apply 共用）----

    static bool Writable(ulong addr, int len)
    {
        if (TestMap != null)
            return addr >= TestBase && addr - TestBase + (ulong)len <= (ulong)TestMap.LongLength;
        return VirtualQuery(addr, out var m, MbiSize) != 0
            && m.State == MEM_COMMIT && addr >= m.BaseAddress
            && addr - m.BaseAddress + (ulong)len <= m.RegionSize
            && (m.Protect & 0xFF) is 0x04 or 0x40;   // READWRITE / EXECUTE_READWRITE
    }

    /// <summary>进程内直写（游戏线程上就是普通指针写）。目标不可写 → 静默丢弃并记日志——
    /// 注入组件纪律：绝不崩游戏；调用方负责在写入后读回校验（trampoline/proof 都会）。</summary>
    public static void WriteU64(ulong addr, ulong v)
    {
        if (TestMap != null)
        {
            if (Writable(addr, 8)) BitConverter.TryWriteBytes(TestMap.AsSpan((int)(addr - TestBase), 8), v);
            return;
        }
        if (!Writable(addr, 8)) { AgentState.Log($"WriteU64 to unwritable 0x{addr:X} dropped"); return; }
        *(ulong*)addr = v;
    }

    public static void WriteU32(ulong addr, uint v)
    {
        if (TestMap != null)
        {
            if (Writable(addr, 4)) BitConverter.TryWriteBytes(TestMap.AsSpan((int)(addr - TestBase), 4), v);
            return;
        }
        if (!Writable(addr, 4)) { AgentState.Log($"WriteU32 to unwritable 0x{addr:X} dropped"); return; }
        *(uint*)addr = v;
    }

    // TestMap 模式下的假分配：假地址 → 内容副本（测试断言 trampoline 写出的字节用）。
    internal static readonly Dictionary<ulong, byte[]> TestAllocs = new();
    static ulong nextTestAlloc = 0x7F00_0000_0000;

    /// <summary>分配 RW 内存写入新 buffer（旧 buffer 永不释放——S3 旧帧安全）。0 = 失败。</summary>
    public static ulong AllocRW(byte[] data)
    {
        if (TestMap != null)
        {
            ulong fake = nextTestAlloc;
            nextTestAlloc += 0x1000;
            TestAllocs[fake] = data.ToArray();
            return fake;
        }
        ulong p = VirtualAlloc(0, (nuint)data.Length, 0x3000 /*MEM_COMMIT|MEM_RESERVE*/, 0x04 /*PAGE_READWRITE*/);
        if (p == 0) { AgentState.Log("VirtualAlloc failed"); return 0; }
        Marshal.Copy(data, 0, (nint)p, data.Length);
        return p;
    }
}
