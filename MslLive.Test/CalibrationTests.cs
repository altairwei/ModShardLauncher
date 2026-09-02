using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>ReportCalibration：合成 RValue 缓冲区在不同偏移写 $4D 打包值，逐个锁定与全不中路径。</summary>
public unsafe class CalibrationTests : IDisposable
{
    public CalibrationTests() { AgentState.ResetForTest(); ReportCalibration.ResetForTest(); }
    public void Dispose() { AgentState.ResetForTest(); ReportCalibration.ResetForTest(); }

    // spriteFirst=1000 (0x3E8), pathFirst=64 (0x40) → 0x4D000000 | (1000<<8) | 64
    const uint Packed = 0x4D03E840;

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(4)]
    [InlineData(16)]
    public void OnReport_LocksCorrectOffset(int off)
    {
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(uint*)(buf + off) = Packed;

        ReportCalibration.OnReport(buf, 1);

        Assert.True(ReportCalibration.Ready);
        Assert.Equal(1000, AgentState.Blanks.SpriteFirst);
        Assert.Equal(64, AgentState.Blanks.PathFirst);
    }

    [Fact]
    public void OnReport_NoTag_FailsClosed()
    {
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0x11;   // 无 $4D 顶字节

        ReportCalibration.OnReport(buf, 1);

        Assert.False(ReportCalibration.Ready);
        Assert.Equal(-1, AgentState.Blanks.SpriteFirst);
        Assert.Contains("report calibration failed", AgentState.Status);
    }

    [Fact]
    public void OnReport_NullArgs_Or_NoArgs_Ignored()
    {
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(uint*)buf = Packed;

        ReportCalibration.OnReport(null, 1);   // null args → 忽略
        ReportCalibration.OnReport(buf, 0);    // argc<1 → 忽略

        Assert.False(ReportCalibration.Ready);
        Assert.Equal("ok", AgentState.Status);   // 未触发 Fail
    }

    [Fact]
    public void OnReport_AfterReady_IsIdempotent()
    {
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(uint*)buf = Packed;
        ReportCalibration.OnReport(buf, 1);
        Assert.True(ReportCalibration.Ready);

        // 第二次上报（别的值）不得覆盖已锁定的校准
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(uint*)(buf + 16) = 0x4D000101u;
        ReportCalibration.OnReport(buf, 1);
        Assert.Equal(1000, AgentState.Blanks.SpriteFirst);
        Assert.Equal(64, AgentState.Blanks.PathFirst);
    }

    /// <summary>读 agent.log 的活句柄文件：静态 writer 持有写句柄，须显式共享读
    /// （File.ReadAllText 的默认共享模式会被拒）。临时目录遗留不删（writer 持有句柄）。</summary>
    static string ReadAgentLog(string dir)
    {
        using var fs = new FileStream(Path.Combine(dir, "msllive", "agent.log"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>#19（真机 11:03 全沉默实证）：旧版 args==null/argc<1 早退与「report 从未
    /// 进入」不可判别——入口证据必须打在最前面（Ready/failed 闩锁之后、守卫早退之前），
    /// 且首调用一条（闩锁防每帧刷屏）。临时目录遗留不删（静态 log writer 持有句柄）。</summary>
    [Fact]
    public void OnReport_FirstCall_LogsEntryEvidence_EvenOnSilentPaths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msl-cali-" + Guid.NewGuid().ToString("N"));
        AgentState.InitForTest(dir);

        ReportCalibration.OnReport(null, 0);   // 旧版全静音的早退路径：null args + argc<1

        string log = ReadAgentLog(dir);
        Assert.Contains("report entry:", log);
        Assert.Contains("argc=0", log);
        Assert.Equal("ok", AgentState.Status);   // 守卫早退仍不 Fail
    }

    /// <summary>#19：入口证据必须含候选窗原始 hex（offset:value 对），真机定校准为何
    /// 全不中。校准锁定行为不变。</summary>
    [Fact]
    public void OnReport_FirstCall_LogsCandidateWindowHex()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msl-cali-" + Guid.NewGuid().ToString("N"));
        AgentState.InitForTest(dir);
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(uint*)(buf + 8) = Packed;

        ReportCalibration.OnReport(buf, 1);

        string log = ReadAgentLog(dir);
        Assert.Contains("report entry:", log);
        Assert.Contains("8:4D03E840", log);
        Assert.True(ReportCalibration.Ready);
        Assert.Equal(1000, AgentState.Blanks.SpriteFirst);
    }

    /// <summary>#19：全不中路径的 Fail 闩锁——第二帧起静音（旧版每帧 Fail 会刷爆
    /// agent.log：gate 常开后 60 行/秒）。fail-closed 语义不变（Blanks 永 -1）。</summary>
    [Fact]
    public void OnReport_NoTag_FailsOnce_ThenLatched()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msl-cali-" + Guid.NewGuid().ToString("N"));
        AgentState.InitForTest(dir);
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0x11;

        ReportCalibration.OnReport(buf, 1);
        ReportCalibration.OnReport(buf, 1);   // 第二帧：闩锁后全静音
        ReportCalibration.OnReport(buf, 1);

        string log = ReadAgentLog(dir);
        Assert.Equal(1, log.Split("report calibration failed").Length - 1);
        Assert.False(ReportCalibration.Ready);
        Assert.Equal(-1, AgentState.Blanks.SpriteFirst);
    }
}
