using System.Runtime.InteropServices;
using System.Text;

namespace MslLive.Agent;

public static unsafe class NativeThunks
{
    // TRoutine 形态以 Task 11 addresses.h 注释为准（result/self/other/argc/args 五参，
    // x64 单调用约定）；读参数才需要布局——apply 什么都不读，report 只读 args[0]（自校准）。

    [UnmanagedCallersOnly]
    public static void Apply(void* result, void* self, void* other, int argc, void* args)
    {
        // 游戏线程每帧经 trampoline 进这里：Phase 2 整批指针写（无 pending 时立刻返回）。
        // 绝不抛托管异常出 UCO 边界——Pump 内部不自抛，这里再兜一层保险（崩 = 崩游戏）。
        try { ApplyEngine.Pump(); }
        catch (Exception ex) { AgentState.Log("pump guard: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    public static void Report(void* result, void* self, void* other, int argc, void* args)
        => ReportCalibration.OnReport(args, argc);
}

public static class NativeRegistration
{
    public static int ApplyIndex = -1, ReportIndex = -1;

    public static unsafe void RegisterAll()
    {
        // Function_Add(name, funcptr, argc)——**3 参形态**，Task 11 Step 3 实测无 r9 flag
        // （计划模板写了 4 参，此处按 addresses.h 的 typedef 修正；x64 多传无害但按实测写干净）。
        var add = (delegate* unmanaged[Stdcall]<byte*, void*, int, void>)AgentState.FunctionAdd;
        // 方法组 → void* 必须经类型化函数指针中转（CS8812）；UCO 默认约定 ≠ Stdcall 元数据（CS8786），
        // x64 硬件层面同一 ABI，用裸 unmanaged 与 UCO 默认对齐。
        delegate* unmanaged<void*, void*, void*, int, void*, void> applyFn = &NativeThunks.Apply;
        delegate* unmanaged<void*, void*, void*, int, void*, void> reportFn = &NativeThunks.Report;
        add((byte*)Marshal.StringToHGlobalAnsi("msl_live_apply"), (void*)applyFn, 0);
        add((byte*)Marshal.StringToHGlobalAnsi("msl_live_report"), (void*)reportFn, 1);
        // 自家 thunk 在模块外——Rescan 前报备，否则 fn 区间校验拒记录（fix-loop #6）
        Registry.OwnFn((ulong)applyFn);
        Registry.OwnFn((ulong)reportFn);
        Registry.Rescan();
        ApplyIndex = Registry.IndexOf("msl_live_apply");
        ReportIndex = Registry.IndexOf("msl_live_report");
        AgentState.Log($"natives registered: apply={ApplyIndex}, report={ReportIndex}");
        if (ApplyIndex < 0 || ReportIndex < 0) AgentState.Fail("native registration rescan failed");
    }
}

/// <summary>RValue 偏移自校准：上报打包值高字节固定 tag $4D（Task 8）。
/// 首个上报到来时逐个候选布局试读 args[0]，(v & 0xFF000000)==0x4D000000 者锁定；
/// 全部不中 → AgentState.Fail（fail-closed，blanks 永远 -1 → MSL 拒会话）。</summary>
public static class ReportCalibration
{
    static int lockedOffset = -1;
    static bool failed, entryLogged;
    public static bool Ready { get; private set; }

    public static unsafe void OnReport(void* args, int argc)
    {
        if (Ready || failed) return;
        // #19 诊断（真机 11:03 全沉默实证）：旧版 args==null/argc<1 早退与「report 从未
        // 进入」在 agent.log 上不可区分——入口证据打在最前面（首调用一条，闩锁防每帧
        // 刷屏），候选窗原始 hex 一次抓全：真机据此判别 调用面断（零 entry 行） vs
        // 校准面断（entry 行里没有一个 4Dxxxxxx）。
        if (!entryLogged)
        {
            entryLogged = true;
            var sb = new StringBuilder();
            if (args == null) sb.Append("<null>");
            else foreach (int off in new[] { 0, 4, 8, 12, 16, 24 })
                sb.Append(off).Append(':').Append((*(uint*)((byte*)args + off)).ToString("X8")).Append(' ');
            AgentState.Log($"report entry: args=0x{(ulong)args:X} argc={argc} cand[{sb}]");
        }
        if (args == null || argc < 1) return;
        foreach (int off in new[] { 0, 8, 4, 16 })
        {
            uint v = *(uint*)((byte*)args + off);
            if ((v & 0xFF000000) == 0x4D000000)
            {
                lockedOffset = off;
                AgentState.Blanks.Set((int)((v >> 8) & 0xFFFF), (int)(v & 0xFF));
                Ready = true;
                AgentState.Log($"report calibrated: offset={off}");
                return;
            }
        }
        // Fail 闩锁：gate 常开后旧版每帧 Fail 会以 60 行/秒刷爆 agent.log。
        // fail-closed 语义不变（Blanks 永 -1 → MSL 拒会话），详见 entry 行。
        failed = true;
        AgentState.Fail("report calibration failed: no offset matched tag $4D (see report entry line)");
    }

    internal static void ResetForTest() { lockedOffset = -1; Ready = false; failed = false; entryLogged = false; }
}
