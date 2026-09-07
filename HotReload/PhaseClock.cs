using System;
using System.Diagnostics;
using Serilog;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 1] 分段计时：Dispose 时打 [perf] 日志。纯日志零行为——校准 v2 收益分布
/// （spec §11.1：LoadFile/SaveFile/PatchMods 分段 + 反编译/编译计数拆分）。</summary>
public sealed class PhaseClock : IDisposable
{
    readonly string stage;
    readonly Stopwatch sw;

    public PhaseClock(string stage)
    {
        this.stage = stage;
        sw = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        sw.Stop();
        Log.Information("[perf] {Stage}: {Elapsed} ms", stage, sw.ElapsedMilliseconds);
    }
}

/// <summary>[v2 Task 1] DSL 层反编译/编译计数（进程累计观测；Task 4 起 LoadGML/Save 改道
/// FastText 后由 FastText.Read/CompileEntry 统一计数）。</summary>
public static class PerfCounters
{
    public static int DecompileCount { get; private set; }
    public static int CompileCount { get; private set; }
    public static void CountDecompile() => DecompileCount++;
    public static void CountCompile() => CompileCount++;
    public static void Reset()
    {
        DecompileCount = 0;
        CompileCount = 0;
    }
}
