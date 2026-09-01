using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>Trampoline.Install 的合成内存端到端。#16b 子条目模型：每个 stub = 根+子两条
/// 节点/执行记录，共享同一 68B wrapper blob（S2④ 原文实证：两条执行记录 +0x18 同指 BUF；
/// 根入口 0 = B→绑定尾，子入口 4 = 函数体）。dummy 形态 = #16a function 声明编译产物
/// （tw-shape 探针 root len=68 + StubDummy_CompilesTo_WrapperWithPushiZeroRetVBody 钉版）。
/// 成功例断言两条执行记录 +0x08/+0x18 都换成同一新 buffer 且内容逐字节正确
/// （apply 无参 28B / report 带 argument0 36B，布局 [exit 4B @0][体 @4]）、幂等不重装；
/// 失败例钉 fail-closed：wrapper 头/长度不符、原生未注册、节点缺失时绝不改执行记录。</summary>
public class TrampolineTests : IDisposable
{
    const ulong SIG = 0x1406BE508;
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    // apply / report 各一组 根+子 节点/记录/名字；根与子共享同一 buffer（S2④）
    const ulong ApplyNode = 0x10100, ApplyRecord = 0x10800, ApplyName = 0x10C00, ApplyBuf = 0x11000;
    const ulong ApplyChildNode = 0x10300, ApplyChildRecord = 0x10A00, ApplyChildName = 0x10E00;
    const ulong ReportNode = 0x10200, ReportRecord = 0x10900, ReportName = 0x10D00, ReportBuf = 0x11100;
    const ulong ReportChildNode = 0x10400, ReportChildRecord = 0x10B00, ReportChildName = 0x10F00;

    /// <summary>#16a function 声明 stub 的 wrapper blob（68B）。头 20B = [B jump=5] +
    /// return-0 收尾（pushi.e 0 / conv.i.v / ret.v）+ [exit.i T1=Int32]——Trampoline 的形状
    /// 校验面（len==68 && 头 20B 全等）；后 48B = 绑定尾 9 指令（push.v fnref/conv/pushi.e -1/
    /// conv/call.v method/dup/pushi.e -6/pop.v.v/popz.v），操作数含运行时 codeId，校验不读，
    /// fixture 以零占位。</summary>
    static byte[] WrapperDummy()
    {
        var head = new byte[]
        {
            0x05, 0x00, 0x00, 0xB6,   // b +5（根执行时跳过 body+exit 直达绑定尾）
            0x00, 0x00, 0x0F, 0x84,   // pushi.e 0
            0x00, 0x00, 0x52, 0x07,   // conv.i.v
            0x00, 0x00, 0x05, 0x9C,   // ret.v
            0x00, 0x00, 0x02, 0x9D,   // exit.i（T1=Int32）
        };
        return head.Concat(new byte[48]).ToArray();
    }

    /// <summary>apply trampoline（28B）：[exit 4B @0][call.v #900 + popz.v + return-0 收尾 @4]。
    /// 根入口 0 = exit（安全 no-op）；子入口 4（StartOff 实证）= call 面。</summary>
    static readonly byte[] ApplyTrampoline =
    {
        0x00, 0x00, 0x02, 0x9D,                           // exit.i
        0x00, 0x00, 0x02, 0xD9, 0x84, 0x03, 0x00, 0x00,   // call.v msl_live_apply → 注册表 900
        0x00, 0x00, 0x05, 0x9E,                           // popz.v
        0x00, 0x00, 0x0F, 0x84, 0x00, 0x00, 0x52, 0x07, 0x00, 0x00, 0x05, 0x9C,
    };

    /// <summary>report trampoline（36B）：[exit 4B @0][push.v argument0 + call.v #901(argc=1) +
    /// popz.v + return-0 收尾 @4]。argument0 指令字 F7 FF 05 C0 = TypeInst Arg(-9)
    /// （vanilla [5] 实证；旧 -15 是 F1 FF…）；操作数 5D 00 00 A0 = builtin 93 | RefTop 0xA0。</summary>
    static readonly byte[] ReportTrampoline =
    {
        0x00, 0x00, 0x02, 0x9D,                           // exit.i
        0xF7, 0xFF, 0x05, 0xC0, 0x5D, 0x00, 0x00, 0xA0,   // push.v argument0（inst=-9，builtin 93）
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

    static void PlantNode(ulong node, ulong record, ulong nameAt, ulong bufAt,
        string name, uint startOff, uint recLen, byte[] live)
    {
        W64(node, SIG);
        W32(node + 0x64, 0x00FFFFFF);
        W64(node + 0x68, record);
        W64(record, EXEC);
        W32(record + 0x08, recLen);
        W32(record + 0x0C, 0);
        W64(record + 0x18, bufAt);
        W64(node + 0x80, nameAt);
        Encoding.ASCII.GetBytes(name).CopyTo(Mem.TestMap!, (int)(nameAt - Base));
        W32(node + 0x88, 1);        // CodeId（值不参与 trampoline 逻辑）
        W32(node + 0x9C, startOff);
        W32(node + 0xA0, 0);
        W32(node + 0xA4, 0);
        live.CopyTo(Mem.TestMap!, (int)(bufAt - Base));
    }

    /// <summary>种一对 根+子 节点：共享同一 buffer 与同一 live 内容（S2④ 父子双记录）。
    /// 根记录 len=68（整 blob）；子记录 len=64（子跨度，运行时读到的典型形态——交换后同被覆写）。</summary>
    static void PlantStubPair(ulong node, ulong record, ulong nameAt, string name,
        ulong childNode, ulong childRecord, ulong childNameAt, string childName,
        ulong bufAt, byte[] live)
    {
        PlantNode(node, record, nameAt, bufAt, name, 0, (uint)live.Length, live);
        PlantNode(childNode, childRecord, childNameAt, bufAt, childName, 4, (uint)live.Length - 4, live);
    }

    static void PlantBothStubs(byte[]? applyLive = null, byte[]? reportLive = null)
    {
        var apply = applyLive ?? WrapperDummy();
        var report = reportLive ?? WrapperDummy();
        PlantStubPair(ApplyNode, ApplyRecord, ApplyName, Trampoline.ApplyName,
            ApplyChildNode, ApplyChildRecord, ApplyChildName, "gml_Script_" + Trampoline.ApplyName,
            ApplyBuf, apply);
        PlantStubPair(ReportNode, ReportRecord, ReportName, Trampoline.ReportName,
            ReportChildNode, ReportChildRecord, ReportChildName, "gml_Script_" + Trampoline.ReportName,
            ReportBuf, report);
        Assert.Equal(4, NodeIndex.Build());
    }

    static int FakeRegistry(string name) =>
        name == Trampoline.ApplyName ? 900 : name == Trampoline.ReportName ? 901 : -1;

    [Fact]
    public void Install_SwapsBothRecordsOfEachStub_AndWritesExpectedBytes()
    {
        PlantBothStubs();
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.True(Trampoline.Install(FakeRegistry), Trampoline.LastError);

        // 根/子两条执行记录都换（共享 BufPtr → 同一 newBuf——S2④ 父子双记录）
        ulong applyPtr = R64(ApplyRecord + 0x18);
        Assert.Equal(applyPtr, R64(ApplyChildRecord + 0x18));
        Assert.Equal((uint)ApplyTrampoline.Length, R32(ApplyRecord + 0x08));
        Assert.Equal((uint)ApplyTrampoline.Length, R32(ApplyChildRecord + 0x08));
        Assert.True(Mem.TestAllocs.ContainsKey(applyPtr));
        Assert.Equal(ApplyTrampoline, Mem.TestAllocs[applyPtr]);

        ulong reportPtr = R64(ReportRecord + 0x18);
        Assert.Equal(reportPtr, R64(ReportChildRecord + 0x18));
        Assert.Equal((uint)ReportTrampoline.Length, R32(ReportRecord + 0x08));
        Assert.Equal((uint)ReportTrampoline.Length, R32(ReportChildRecord + 0x08));
        Assert.True(Mem.TestAllocs.ContainsKey(reportPtr));
        Assert.Equal(ReportTrampoline, Mem.TestAllocs[reportPtr]);
    }

    /// <summary>#17 外扫实证（nodescan 普查，mslRoot 67/67 无节点）：运行时 exec 节点按
    /// 「绑定」创建，不按 Code 条目——wrapper 根刻意不入 GlobalInit 且无人指向 → 根 0 节点；
    /// SCPT/FUNC 都指向子 → 索引里只有子 gml_Script_* 一条（StartOff=4，record len=68=
    /// 整 buffer——不是子跨度 64，BufPtr=共享 buffer 基址）。Install 直名 miss 回退子名。</summary>
    [Fact]
    public void Install_RootHasNoNode_ResolvesViaChildEntry()
    {
        PlantNode(ApplyChildNode, ApplyChildRecord, ApplyChildName, ApplyBuf,
            "gml_Script_" + Trampoline.ApplyName, 4, 68, WrapperDummy());
        PlantNode(ReportChildNode, ReportChildRecord, ReportChildName, ReportBuf,
            "gml_Script_" + Trampoline.ReportName, 4, 68, WrapperDummy());
        Assert.Equal(2, NodeIndex.Build());
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.True(Trampoline.Install(FakeRegistry), Trampoline.LastError);

        ulong applyPtr = R64(ApplyChildRecord + 0x18);
        Assert.Equal((uint)ApplyTrampoline.Length, R32(ApplyChildRecord + 0x08));
        Assert.True(Mem.TestAllocs.ContainsKey(applyPtr));
        Assert.Equal(ApplyTrampoline, Mem.TestAllocs[applyPtr]);

        ulong reportPtr = R64(ReportChildRecord + 0x18);
        Assert.Equal((uint)ReportTrampoline.Length, R32(ReportChildRecord + 0x08));
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
        Assert.Equal(applyPtr, R64(ApplyChildRecord + 0x18));
    }

    [Fact]
    public void Install_WrapperHeadMismatch_FailClosed_NoSwap()
    {
        var badDummy = WrapperDummy();
        badDummy[4] = 0x01;   // 头 20B 内的字节被改（编译器形态假设破裂的替身）
        PlantBothStubs(applyLive: badDummy);
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.False(Trampoline.Install(FakeRegistry));
        Assert.Contains(Trampoline.ApplyName, Trampoline.LastError);
        Assert.Contains("wrapper", Trampoline.LastError);
        Assert.Equal(ApplyBuf, R64(ApplyRecord + 0x18));          // apply 根记录原封不动
        Assert.Equal(ApplyBuf, R64(ApplyChildRecord + 0x18));     // apply 子记录原封不动
        Assert.Equal(68u, R32(ApplyRecord + 0x08));
        // report 独立安装（ok &= 语义：单 stub 失败不株连另一个）
        Assert.NotEqual(ReportBuf, R64(ReportRecord + 0x18));
    }

    [Fact]
    public void Install_DummyWrongLength_Fails()
    {
        // 垫片形态的 80B dummy（loader/slot stub 垫 var _t 后的长度）——apply stub 期望 68B
        var padded = WrapperDummy().Concat(new byte[12]).ToArray();
        PlantBothStubs(applyLive: padded);
        NativeRegistration.ApplyIndex = 900;
        NativeRegistration.ReportIndex = 901;

        Assert.False(Trampoline.Install(FakeRegistry));
        Assert.Contains("wrapper", Trampoline.LastError);
        Assert.Equal(ApplyBuf, R64(ApplyRecord + 0x18));
        Assert.Equal(ApplyBuf, R64(ApplyChildRecord + 0x18));
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
