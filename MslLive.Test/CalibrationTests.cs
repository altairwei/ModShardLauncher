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
}
