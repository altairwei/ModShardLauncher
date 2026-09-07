using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

public class PhaseClockTests : IDisposable
{
    public void Dispose() => PerfCounters.Reset();

    [Fact]
    public void PhaseClock_DisposeIsSafe()
    {
        // 纯日志契约（Dispose 打 [perf] 行）：构造+释放不抛 = 契约成立。
        // 插桩后的计时证据见 Task 1 Step 5-7 的回归与真机日志。
        using var clock = new PhaseClock("test-stage");
    }

    [Fact]
    public void PerfCounters_TrackDecompileAndCompile()
    {
        PerfCounters.Reset();
        PerfCounters.CountDecompile();
        PerfCounters.CountCompile();
        Assert.Equal(1, PerfCounters.DecompileCount);
        Assert.Equal(1, PerfCounters.CompileCount);
    }
}
