using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>Trampoline.Install 的合成内存端到端：两个 dummy stub 节点（12 字节 return-0 活 buffer，
/// 形态由 ModShardLauncherTest.StubDummy_CompilesTo_PushiZeroRetV 钉版）。
/// 成功例断言执行记录 +0x08/+0x18 被换成新 buffer 且内容逐字节正确（apply 无参 24B /
/// report 带 argument0 32B）、幂等不重装；失败例钉 fail-closed：收尾形态不符 / 原生未注册 /
/// 节点缺失时绝不改执行记录。</summary>
public class TrampolineTests : IDisposable
{
    const ulong SIG = 0x1406BE508;
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    // apply / report 各一组 节点/记录/名字/buffer
    const ulong ApplyNode = 0x10100, ApplyRecord = 0x10800, ApplyName = 0x10C00, ApplyBuf = 0x11000;
    const ulong ReportNode = 0x10200, ReportRecord = 0x10900, ReportName = 0x10D00, ReportBuf = 0x11100;

    /// <summary>"return 0;" 的编译形态：pushi.e 0 | conv.i.v | ret.v（12 字节钉版）。</summary>
    static readonly byte[] DummyTail =
        { 0x00, 0x00, 0x0F, 0x84, 0x00, 0x00, 0x52, 0x07, 0x00, 0x00, 0x05, 0x9C };

    /// <summary>apply trampoline：call.v #900 + popz.v + return-0 收尾。</summary>
    static readonly byte[] ApplyTrampoline =
    {
        0x00, 0x00, 0x02, 0xD9, 0x84, 0x03, 0x00, 0x00,   // call.v msl_live_apply → 注册表 900
        0x00, 0x00, 0x05, 0x9E,                           // popz.v
        0x00, 0x00, 0x0F, 0x84, 0x00, 0x00, 0x52, 0x07, 0x00, 0x00, 0x05, 0x9C,
    };

    /// <summary>report trampoline：push.v argument0 + call.v #901(argc=1) + popz.v + return-0 收尾。</summary>
    static readonly byte[] ReportTrampoline =
    {
        0xF1, 0xFF, 0x05, 0xC0, 0x5D, 0x00, 0x00, 0xA0,   // push.v argument0（inst=-15，builtin 93）
        0x01, 0x00, 0x02, 0xD9, 0x85, 0x03, 0x00, 0x00,   // call.v msl_live_report argc=1 → 注册表 901
        0x00, 0x00, 0x05, 0x9E,                           // popz.v
        0x00, 0x00, 0x0F, 0x84, 0x00, 0x00, 0x52, 0x07, 0x00, 0x00, 0x05, 0x9C,
    };

    public TrampolineTests()
    {
        Mem.TestMap = new byte[0x2000];
        Mem.TestBase = Base;
        Mem.TestAllocs.Clear();
        AgentState.ResetForTest();
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        Mem.TestAllocs.Clear();
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
        NativeRegistration.ApplyIndex = NativeRegistration.ReportIndex = -1;
        AgentState.ResetForTest();
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static uint R32(ulong addr) => BitConverter.ToUInt32(Mem.TestMap!, (int)(addr - Base));
    static ulong R64(ulong addr) => BitConverter.ToUInt64(Mem.TestMap!, (int)(addr - Base));

    static void PlantStub(ulong node, ulong record, ulong nameAt, ulong bufAt, string name, byte[] live)
    {
        W64(node, SIG);
        W32(node + 0x64, 0x00FFFFFF);
        W64(node + 0x68, record);
        W64(record, EXEC);
        W32(record + 0x08, (uint)live.Length);
        W64(record + 0x18, bufAt);
        W64(node + 0x80, nameAt);
        Encoding.ASCII.GetBytes(name).CopyTo(Mem.TestMap!, (int)(nameAt - Base));
        W32(node + 0x88, 1);        // CodeId（值不参与 trampoline 逻辑）
        live.CopyTo(Mem.TestMap!, (int)(bufAt - Base));
    }

    static void PlantBothStubs(byte[]? applyLive = null, byte[]? reportLive = null)
    {
        PlantStub(ApplyNode, ApplyRecord, ApplyName, ApplyBuf, Trampoline.ApplyName, applyLive ?? DummyTail);
        PlantStub(ReportNode, ReportRecord, ReportName, ReportBuf, Trampoline.ReportName, reportLive ?? DummyTail);
        Assert.Equal(2, NodeIndex.Build());
    }

    static int FakeRegistry(string name) =>
        name == Trampoline.ApplyName ? 900 : name == Trampoline.ReportName ? 901 : -1;

    [Fact]
    public void Install_SwapsRecords_AndWritesExpectedBytes()
    {
        PlantBothStubs();
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.True(Trampoline.Install(FakeRegistry), Trampoline.LastError);

        // 执行记录 +0x08=len / +0x18=ptr 已换；新 buffer 在 TestAllocs 且逐字节正确
        Assert.Equal((uint)ApplyTrampoline.Length, R32(ApplyRecord + 0x08));
        ulong applyPtr = R64(ApplyRecord + 0x18);
        Assert.True(Mem.TestAllocs.ContainsKey(applyPtr));
        Assert.Equal(ApplyTrampoline, Mem.TestAllocs[applyPtr]);

        Assert.Equal((uint)ReportTrampoline.Length, R32(ReportRecord + 0x08));
        ulong reportPtr = R64(ReportRecord + 0x18);
        Assert.True(Mem.TestAllocs.ContainsKey(reportPtr));
        Assert.Equal(ReportTrampoline, Mem.TestAllocs[reportPtr]);
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        PlantBothStubs();
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.True(Trampoline.Install(FakeRegistry));
        int allocs = Mem.TestAllocs.Count;
        ulong applyPtr = R64(ApplyRecord + 0x18);
        Assert.True(Trampoline.Install(FakeRegistry));   // 重连重发 proof 不会把 trampoline 当 dummy 判死
        Assert.Equal(allocs, Mem.TestAllocs.Count);      // 不二次分配
        Assert.Equal(applyPtr, R64(ApplyRecord + 0x18));
    }

    [Fact]
    public void Install_TailMismatch_FailClosed_NoSwap()
    {
        var badDummy = DummyTail.ToArray();
        badDummy[0] = 0x01;   // 编译器形态假设破裂的替身
        PlantBothStubs(applyLive: badDummy);
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.False(Trampoline.Install(FakeRegistry));
        Assert.Contains(Trampoline.ApplyName, Trampoline.LastError);
        Assert.Contains("return-0 shape", Trampoline.LastError);
        Assert.Equal(ApplyBuf, R64(ApplyRecord + 0x18));          // apply 记录原封不动
        Assert.Equal((uint)DummyTail.Length, R32(ApplyRecord + 0x08));
        // report 独立安装（ok &= 语义：单 stub 失败不株连另一个）
        Assert.NotEqual(ReportBuf, R64(ReportRecord + 0x18));
    }

    [Fact]
    public void Install_NativeNotRegistered_Fails()
    {
        PlantBothStubs();
        NativeRegistration.ApplyIndex = -1;      // 注册没发生
        NativeRegistration.ReportIndex = 901;

        Assert.False(Trampoline.Install(FakeRegistry));
        Assert.Contains("native not registered", Trampoline.LastError);
        Assert.Equal(ApplyBuf, R64(ApplyRecord + 0x18));
    }

    [Fact]
    public void Install_StubNodeMissing_Fails()
    {
        NodeIndex.Build();   // 空 TestMap
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.False(Trampoline.Install(FakeRegistry));
        Assert.Contains("stub node not found", Trampoline.LastError);
        Assert.Empty(Mem.TestAllocs);
    }
}
