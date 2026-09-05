using System.Diagnostics;
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

    /// <summary>整块读（proof 读回活 buffer 用）。守卫失败 → 空数组（fail-closed：调用方按长度不符处理）。
    /// TestMap 模式下假分配地址（TestAllocs）也可读——#35 重推用例：首次 commit 把 buffer/
    /// 表换到假分配地址，二次推送的自证要能读回它们（生产对应物是真分配内存，天然可读）。</summary>
    public static byte[] ReadBytes(ulong addr, int len)
    {
        if (len <= 0 || addr == 0) return Array.Empty<byte>();
        if (TestMap != null)
        {
            if (TestAllocs.TryGetValue(addr, out var alloc))
            {
                var b = new byte[Math.Min(len, alloc.Length)];
                Array.Copy(alloc, b, b.Length);
                return b;
            }
            if (!Readable(addr, len)) return Array.Empty<byte>();
            var tb = new byte[len];
            Array.Copy(TestMap, (int)(addr - TestBase), tb, 0, len);
            return tb;
        }
        if (!Readable(addr, len)) return Array.Empty<byte>();
        var buf = new byte[len];
        Marshal.Copy((nint)addr, buf, 0, len);
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

    // ---- fix-loop #27：换入缓冲改走游戏自己的调试堆分配器 ----
    // 游戏自带带统计的调试堆：DebugAlloc=malloc(size+0x20)、写头（+0x08=size、
    // +0x0c=0xdeadc0de、+0x10=0xbaadb00b）、返回 raw+0x20。退出遍历的 record 重置
    // （0x140075fc0：vtable 重置 + free(+0x20 表/+0x28 pcmap) + arena 外的 +0x18 buf 走
    // 0x140502cbc thunk 进同一机制）会读 buf-0x20 魔术字——裸 VirtualAlloc 缓冲前是
    // 未提交页必 AV（12:08/13:50/16:36 三例同形 @0x1404CBB74）。用游戏的分配器 =
    // by construction 可释放（游戏自己的分配/释放是成对的）。

    /// <summary>分配器头写序列（.text 全库唯一，离线实证）：mov [rax+0xc],0xdeadc0de ;
    /// mov [rax+0x10],0xbaadb00b。按特征不硬编码 VA——游戏更新只挪地址，魔术字与
    /// .pdata 结构不动。</summary>
    static readonly byte[] GameAllocAob =
    {
        0xC7, 0x40, 0x0C, 0xDE, 0xC0, 0xAD, 0xDE,   // mov [rax+0xc], 0xdeadc0de
        0xC7, 0x40, 0x10, 0x0B, 0xB0, 0xAD, 0xBA,   // mov [rax+0x10], 0xbaadb00b
    };

    /// <summary>测试缝（fix #27）：非空时完全替代生产「定位+调用」（TestMap=null 分支）。</summary>
    internal static Func<byte[], ulong>? GameAllocOverride;

    static readonly object gameAllocGate = new();
    static ulong gameAllocFn;      // 0 = 未定位或定位失败
    static bool gameAllocTried;

    /// <summary>映像区段扫描步长：.text 几 MB，分块读（块间重叠 pattern-1 字节、起点归属
    /// 不重叠——每块只报 start &lt; 步进界的命中，跨界 pattern 由下一块读头的覆盖区接住）。</summary>
    const uint ImageScanStep = 0x40000;

    /// <summary>映像区段 AOB 扫描。.text 是 MEM_IMAGE（R-X），Regions() 只枚举 MEM_PRIVATE
    /// RW 堆扫不到——所以按节边界 ReadBytes 直读。≥2 命中即早停（唯一性由调用方把关）。</summary>
    static List<ulong> ScanImageAob(ulong addr, uint len, byte[] pattern)
    {
        var hits = new List<ulong>();
        uint pos = 0;
        while (pos < len)
        {
            bool last = len - pos <= ImageScanStep;
            uint take = last ? len - pos : ImageScanStep + (uint)pattern.Length - 1;
            var buf = ReadBytes(addr + pos, (int)take);
            if (buf.Length < pattern.Length) return hits;   // 守卫拦截 → 到此为止（调用方唯一性兜底）
            int scanEnd = last ? buf.Length - pattern.Length + 1 : (int)ImageScanStep;
            for (int i = 0; i < scanEnd; i++)
            {
                int k = 0;
                while (k < pattern.Length && buf[i + k] == pattern[k]) k++;
                if (k == pattern.Length)
                {
                    hits.Add(addr + pos + (uint)i);
                    if (hits.Count > 1) return hits;
                }
            }
            pos += last ? len - pos : ImageScanStep;
        }
        return hits;
    }

    /// <summary>在加载映像里定位游戏调试堆分配函数（fix #27）。DOS→COFF→节表 解析
    /// .text/.pdata；AOB 唯一命中；.pdata RUNTIME_FUNCTION（Begin 升序）二分取含命中的
    /// 函数边界。不猜 prologue（x64 PE 必有 .pdata）。0 = 失败（调用方 fail-closed）。</summary>
    public static ulong LocateGameAlloc(ulong imageBase)
    {
        try
        {
            uint peOff = ReadU32(imageBase + 0x3C);
            if (peOff is 0 or > 0x1000) return 0;
            ulong coff = imageBase + peOff + 4;
            uint hdr = ReadU32(coff);                          // Machine | NumberOfSections<<16
            if ((hdr & 0xFFFF) != 0x8664) return 0;            // x64 only
            int nsec = (int)(hdr >> 16);
            if (nsec is 0 or > 96) return 0;
            uint optSize = ReadU32(coff + 16) & 0xFFFF;
            ulong secTab = imageBase + peOff + 24 + optSize;
            ulong textVa = 0, pdataVa = 0; uint textSz = 0, pdataSz = 0;
            for (int i = 0; i < nsec; i++)
            {
                ulong h = secTab + (ulong)(i * 40);
                string name = ReadCString(h, 8);
                if (name == ".text") { textSz = ReadU32(h + 8); textVa = imageBase + ReadU32(h + 12); }
                else if (name == ".pdata") { pdataSz = ReadU32(h + 8); pdataVa = imageBase + ReadU32(h + 12); }
            }
            if (textVa == 0 || textSz == 0 || pdataVa == 0 || pdataSz < 12) return 0;
            var hits = ScanImageAob(textVa, textSz, GameAllocAob);
            if (hits.Count != 1) return 0;
            uint hitRva = (uint)(hits[0] - imageBase);
            // .pdata 二分：最后一条 Begin <= hitRva 的条目
            uint n = pdataSz / 12;
            uint lo = 0, hi = n;
            while (lo < hi)
            {
                uint mid = lo + (hi - lo) / 2;
                if (ReadU32(pdataVa + (ulong)mid * 12) <= hitRva) lo = mid + 1;
                else hi = mid;
            }
            if (lo == 0) return 0;
            ulong e = pdataVa + (ulong)(lo - 1) * 12;
            uint begin = ReadU32(e), end = ReadU32(e + 4);
            if (!(begin <= hitRva && hitRva < end)) return 0;
            if (end - begin > 0x2000) return 0;    // 分段函数/异常条目 → 不认（fail-closed）
            return imageBase + begin;
        }
        catch { return 0; }
    }

    /// <summary>分配 RW 内存写入新 buffer（旧 buffer 永不释放——S3 旧帧安全，语义不变）。
    /// 0 = 失败。fix #27：生产路径改走游戏分配器（懒定位一次缓存）——换入缓冲带统计头，
    /// 退出遍历的 record 重置全程干净；定位/分配失败 = fail-closed 拒分配（trampoline/
    /// apply 各自失败路径带明确 reason 上报）。</summary>
    public static ulong AllocRW(byte[] data)
    {
        if (TestMap != null)
        {
            ulong fake = nextTestAlloc;
            nextTestAlloc += 0x1000;
            TestAllocs[fake] = data.ToArray();
            return fake;
        }
        if (GameAllocOverride != null) return GameAllocOverride(data);
        if (!gameAllocTried)
        {
            lock (gameAllocGate)
            {
                if (!gameAllocTried)
                {
                    gameAllocTried = true;
                    try
                    {
                        var main = Process.GetCurrentProcess().MainModule;
                        gameAllocFn = main == null ? 0 : LocateGameAlloc((ulong)main.BaseAddress);
                    }
                    catch (Exception ex) { gameAllocFn = 0; AgentState.Log($"game alloc 定位异常: {ex.Message}"); }
                    AgentState.Log(gameAllocFn == 0
                        ? "游戏分配器定位失败（AOB/.pdata）——热装分配不可用（fail-closed）"
                        : $"game alloc located @ 0x{gameAllocFn:X}");
                }
            }
        }
        if (gameAllocFn == 0) return 0;
        ulong p = CallGameAlloc(gameAllocFn, (uint)data.Length);
        if (p == 0) { AgentState.Log($"game alloc failed (size={data.Length})"); return 0; }
        Marshal.Copy(data, 0, (nint)p, data.Length);
        return p;
    }

    /// <summary>调游戏 DebugAlloc(rcx=size, r9b=flag)。rdx/r8 未用（反汇编钉版）；flag=0 与
    /// 游戏自己的对齐分配器调用形态一致（xor r9d,r9d）。</summary>
    static ulong CallGameAlloc(ulong fn, uint size)
    {
        var f = (delegate* unmanaged[Stdcall]<uint, nint, nint, byte, nint>)(void*)(nint)fn;
        return (ulong)f(size, 0, 0, 0);
    }
}
