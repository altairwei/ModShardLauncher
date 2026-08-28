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

    /// <summary>MEM_COMMIT ∧ MEM_PRIVATE ∧ (READWRITE|EXECUTE_READWRITE) ∧ !GUARD 的区域枚举。</summary>
    public static IEnumerable<(ulong Base, ulong Size)> Regions()
    {
        if (TestMap != null) { yield return (TestBase, (ulong)TestMap.LongLength); yield break; }
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

    /// <summary>8 对齐 qword 值扫描（NodeIndex 的 NodeSigFn 撒网）。
    /// 非常规返回 List 而非迭代器：unsafe 指针在迭代器方法里要 C#13，net6 默认 C#10。</summary>
    public static List<ulong> ScanQword(ulong value)
    {
        var hits = new List<ulong>();
        foreach (var (b, size) in Regions())
        {
            if (TestMap != null)   // 合成内存：扫数组，别把假基址当真指针解引用
            {
                for (ulong off = 0; off + 8 <= size; off += 8)
                    if (BitConverter.ToUInt64(TestMap, (int)off) == value)
                        hits.Add(b + off);
                continue;
            }
            byte* p = (byte*)b;
            for (ulong off = 0; off + 8 <= size; off += 8)
                if (*(ulong*)(p + off) == value)
                    hits.Add(b + off);
        }
        return hits;
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
                for (ulong off = 0; off + (ulong)pattern.Length <= size; off++)
                {
                    int i = 0;
                    while (i < pattern.Length && (pattern[i] == null || TestMap[off + (ulong)i] == pattern[i]!.Value)) i++;
                    if (i == pattern.Length) hits.Add(b + off);
                }
                continue;
            }
            byte* p = (byte*)b;
            int n = pattern.Length;
            for (ulong off = 0; off + (ulong)n <= size; off++)
            {
                int i = 0;
                while (i < n && (pattern[i] == null || p[off + (ulong)i] == pattern[i]!.Value)) i++;
                if (i == n) hits.Add(b + off);
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
    /// 不可读 → ""。</summary>
    public static string ReadCString(ulong addr, int max)
    {
        if (addr == 0 || !Readable(addr, 1)) return "";
        var buf = new byte[max];
        int n = 0;
        while (n < max)
        {
            if (!Readable(addr + (ulong)n, 1)) break;
            byte c = TestMap != null ? TestMap[addr - TestBase + (ulong)n] : *(byte*)(addr + (ulong)n);
            if (c == 0) break;
            buf[n++] = c;
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
