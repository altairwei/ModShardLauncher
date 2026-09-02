using System.Runtime.InteropServices;
using System.Text;

namespace MslLive.Agent;

/// <summary>#20 取证：VEH 崩溃捕获器（Dev 组件常驻，不改游戏任何字节）。
/// 背景：#19 修复后真机装上 trampoline 的下一帧内，游戏 GML 错误格式化器
/// （0x1402726A0——引用 'gml_Object_'/'Create Event'/'%s (line %d)' 等串）沿
/// ctx 父链（+0x08）漫步活帧头时撞 0xAABBCCDD magic 断言 int3（0x1402726ED 等 5 处），
/// 主错误消息随进程死亡湮灭。本组件：
///  1) 命中已知断言 int3 → 倒全现场到 msllive/crash-dump.txt（寄存器/ctx 链逐帧
///     帧头 hexdump + 表指针→脚本名反查/trampoline 记录现状/栈区串注记），然后
///     Rip 步过 int3 续行（5 处的下一指令恰为校验通过分支）——让格式化器跑完、
///     游戏正常错误对话框现身，主错误消息可见；
///  2) 其他致命异常（AV/栈溢出/fail-fast）→ 倒一份后放行死（CONTINUE_SEARCH）；
///  3) 良性首chance（.NET 0xE0434352 / C++ 0xE06D7363 等）零处理直接放行。
/// 所有内存访问走 Mem 的 VirtualQuery 守卫读，handler 自身 try 包裹——绝不因取证
/// 反杀游戏。跳过上限 64 次、转储上限 6 份（防漫步失控刷盘）。</summary>
public static unsafe class CrashCapture
{
    // 错误格式化器内 5 处帧头 magic 断言的 int3 地址（离线解剖钉版；续行目标=下一指令）
    static readonly HashSet<ulong> KnownInt3 = new()
    { 0x1402726ED, 0x14027283F, 0x140272868, 0x1402729D8, 0x140272B56 };

    const ulong ImageBase = 0x140000000;
    const ulong ImageLo = ImageBase + 0x1000, ImageHi = ImageBase + 0xA00000;
    const ulong CurCtxGlobal = 0x140A34548;   // 当前 exec ctx 全局（frame-enter 0x14028E440 栈帧上的结构）

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int VecHandler(nint exceptionPointers);

    [DllImport("kernel32.dll")] static extern nint AddVectoredExceptionHandler(uint first, VecHandler handler);

    static VecHandler? handler;   // 静态保活防 GC 收集委托
    static int skips, dumps;

    public static void Install()
    {
        handler = Handler;
        if (AddVectoredExceptionHandler(1, handler) == 0)
            AgentState.Log("crash capture: AddVectoredExceptionHandler failed");
        else
            AgentState.Log("crash capture armed (#20 forensics)");
    }

    static int Handler(nint ep)
    {
        try
        {
            ulong rec = Mem.ReadU64((ulong)ep);        // EXCEPTION_POINTERS.ExceptionRecord
            ulong ctxp = Mem.ReadU64((ulong)ep + 8);   // .ContextRecord
            if (rec == 0 || ctxp == 0) return 0;
            uint code = Mem.ReadU32(rec);              // ExceptionCode
            ulong addr = Mem.ReadU64(rec + 0x10);      // ExceptionAddress

            if (code == 0x80000003 && KnownInt3.Contains(addr))
            {
                if (skips >= 64) return 0;             // 漫步失控：不再续行，放行死
                skips++;
                if (dumps < 6) { dumps++; Dump(ctxp, $"frame-magic assert int3 @0x{addr:X} (skip {skips})"); }
                // 无调试器时 trap 帧 Rip 已过 int3；调试器在场会被回卷到 int3 本身——后者补一步
                if (Mem.ReadU64(ctxp + 0xF8) == addr) Mem.WriteU64(ctxp + 0xF8, addr + 1);
                return -1;                             // EXCEPTION_CONTINUE_EXECUTION
            }
            // 其余致命异常（含未知 int3）：倒一份现场然后放行死
            if (code is 0xC0000005 or 0x80000003 or 0xC00000FD or 0xC0000409 && dumps < 6)
            {
                dumps++;
                Dump(ctxp, $"fatal 0x{code:X8} @0x{addr:X}");
            }
            return 0;                                  // EXCEPTION_CONTINUE_SEARCH
        }
        catch { return 0; }
    }

    // ---- CONTEXT(x64) 寄存器偏移 ----
    static readonly (string Name, int Off)[] Regs =
    {
        ("rax",0x78),("rcx",0x80),("rdx",0x88),("rbx",0x90),("rsp",0x98),("rbp",0xA0),
        ("rsi",0xA8),("rdi",0xB0),("r8",0xB8),("r9",0xC0),("r10",0xC8),("r11",0xD0),
        ("r12",0xD8),("r13",0xE0),("r14",0xE8),("r15",0xF0),("rip",0xF8),
    };

    static void Dump(ulong ctxp, string why)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine($"=== crash-dump {DateTime.Now:HH:mm:ss.fff} — {why} ===");
        var regs = new ulong[Regs.Length];
        for (int i = 0; i < Regs.Length; i++) regs[i] = Mem.ReadU64(ctxp + (ulong)Regs[i].Off);
        for (int i = 0; i < Regs.Length; i++) sb.Append($"{Regs[i].Name}=0x{regs[i]:X}  ");
        sb.AppendLine();
        ulong rsp = regs[4];

        // VM 全局块（解释器记账：当前 ctx / 深度计数 / 两侧保存槽）
        foreach (var g in new[] { 0x140A34548UL, 0x140A34518UL, 0x140A344C8UL, 0x140A34528UL })
            sb.AppendLine($"global [0x{g:X}] = 0x{Mem.ReadU64(g):X}");

        // trampoline 两记录现状（交换面四字段 + buffer 头 40B）——验证崩溃时刻交换完好
        foreach (var nm in new[] { "gml_Script_msl_live_apply", "gml_Script_msl_live_report" })
        {
            if (!NodeIndex.TryGet(nm, out var n)) { sb.AppendLine($"record {nm}: <not indexed>"); continue; }
            ulong r = n.Record;
            ulong len = Mem.ReadU64(r + 0x08), buf = Mem.ReadU64(r + 0x18);
            ulong tbl = Mem.ReadU64(r + 0x20), map = Mem.ReadU64(r + 0x28);
            sb.AppendLine($"record {nm}: rec=0x{r:X} len={len} buf=0x{buf:X} table=0x{tbl:X} pcmap=0x{map:X}");
            sb.AppendLine($"  buf[0..40): {Hex(buf, 40)}");
        }

        // ctx 链漫步（frame-enter：ctx 在函数栈帧上；+0x00 子链/+0x08 父链/+0x58 帧头/
        // +0x90 pc（指令序号）/+0xA0 handler 表；帧头 0x78B 在 VM 栈上）
        sb.AppendLine("--- ctx chain (cur = [0x140A34548], parent = ctx+0x08) ---");
        ulong cur = Mem.ReadU64(CurCtxGlobal);
        var seen = new HashSet<ulong>();
        for (int i = 0; i < 32 && cur != 0 && seen.Add(cur); i++)
        {
            ulong child = Mem.ReadU64(cur), parent = Mem.ReadU64(cur + 8);
            ulong f10 = Mem.ReadU64(cur + 0x10), self = Mem.ReadU64(cur + 0x28);
            ulong other = Mem.ReadU64(cur + 0x30), hdr = Mem.ReadU64(cur + 0x58);
            ulong f88 = Mem.ReadU64(cur + 0x88), pc = Mem.ReadU64(cur + 0x90), tbl = Mem.ReadU64(cur + 0xA0);
            string names = ResolveTable(tbl);
            sb.AppendLine($"ctx[{i}] 0x{cur:X}: child=0x{child:X} parent=0x{parent:X} +10=0x{f10:X} self=0x{self:X} other=0x{other:X}");
            sb.AppendLine($"   hdr=0x{hdr:X} +88=0x{f88:X} pc={pc} table=0x{tbl:X} {names}");
            if (hdr != 0)
            {
                uint magic = Mem.ReadU32(hdr);
                sb.AppendLine($"   hdr magic=0x{magic:X8}{(magic == 0xAABBCCDD ? " OK" : " BAD!")} seq={Mem.ReadU32(hdr + 0xC)} size=0x{Mem.ReadU32(hdr + 0x10):X} f18=0x{Mem.ReadU32(hdr + 0x18):X}");
                sb.AppendLine($"   hdr[0..0x78): {Hex(hdr, 0x78)}");
                sb.AppendLine($"   frame body[hdr+0x78..+0xF8): {Hex(hdr + 0x78, 0x80)}");
            }
            cur = parent;
        }

        // 栈区注记：[rsp, +0x8200) 覆盖错误格式化器 0x80C8 大帧 + 调用方 stub 帧
        // （错误描述结构在调用方 rsp+0x30 ≈ rsp+0x8130——主错误消息本体的最后希望）
        sb.AppendLine("--- stack annotations [rsp, +0x8200): image-range & readable strings ---");
        for (ulong off = 0; off < 0x8200; off += 8)
        {
            ulong slot = rsp + off;
            ulong v = Mem.ReadU64(slot);
            if (v == 0) continue;
            string? note = null;
            if (v is >= ImageLo and < ImageHi)
            {
                note = $"img+0x{v - ImageBase:X}";
                if (v == 0x14027C487) note += " <stubA after crashfn call>";
                else if (v == 0x14027C5F0) note += " <stubB after crashfn call>";
                else if (v is >= 0x1402726A0 and < 0x140273150) note += " <crashfn>";
                string? s = Printable(Mem.ReadCString(v, 96));
                if (s != null) note += $" \"{s}\"";
            }
            else if (v > 0x10000)
            {
                // 堆指针对象：裸串或 YYG RefString（头部 8B 记账后接文本）
                string? s = Printable(Mem.ReadCString(v, 96)) ?? Printable(Mem.ReadCString(v + 8, 96));
                if (s != null) note = $"-> \"{s}\"";
            }
            if (note != null) sb.AppendLine($"  [rsp+0x{off:X}] = 0x{v:X}  {note}");
        }
        sb.AppendLine($"--- caller window raw [rsp+0x8100..+0x8200) ---");
        sb.AppendLine(Hex(rsp + 0x8100, 0x100));

        File.AppendAllText(Path.Combine(AgentState.GameDir, "msllive", "crash-dump.txt"), sb.ToString());
        AgentState.Log($"crash capture: {why} -> crash-dump.txt");
    }

    /// <summary>表指针 → 持有该表的脚本名（活表与旧表都可能，逐节点读 record+0x20 比对）。</summary>
    static string ResolveTable(ulong tbl)
    {
        if (tbl == 0) return "";
        var hits = new List<string>();
        foreach (var kv in NodeIndex.AllNamed)
            if (Mem.ReadU64(kv.Value.Record + 0x20) == tbl)
                hits.Add(kv.Key);
        return hits.Count == 0 ? "(unresolved)" : string.Join(",", hits);
    }

    static string? Printable(string s)
    {
        if (s.Length < 4) return null;
        foreach (char c in s) if (c is < ' ' or >= (char)0x7F) return null;
        return s;
    }

    static string Hex(ulong addr, int len)
    {
        byte[] b = Mem.ReadBytes(addr, len);
        if (b.Length == 0) return "<unreadable>";
        var sb = new StringBuilder(b.Length * 3);
        foreach (byte x in b) sb.Append(x.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
