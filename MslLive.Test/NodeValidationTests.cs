using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>NodeIndex.TryValidate 验证链：Mem.TestMap 合成 0x2000 字节假内存，
/// 合法 / 哨兵错 / vtable 错 / 名字不可读 四例 + Build 集成。</summary>
public class NodeValidationTests : IDisposable
{
    const ulong SIG = 0x1406BE508;      // 与 addresses.h kNodeSigFn 同形（值本身只是扫描靶）
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    public NodeValidationTests()
    {
        Mem.TestMap = new byte[0x2000];
        Mem.TestBase = Base;
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));

    /// <summary>在 node=0x10100 造一个全合法节点（record=0x10800，name=0x10C00 "test_entry"）。</summary>
    static void PlantValidNode()
    {
        W64(0x10100, SIG);                  // 命中点 = 节点 +0x00
        W32(0x10164, 0x00FFFFFF);           // 哨兵
        W64(0x10168, 0x10800);              // → exec record
        W64(0x10800, EXEC);                 // record +0x00 = exec vtable
        W32(0x10808, 712);                  // BufLen
        W64(0x10818, 0x11000);              // BufPtr
        W64(0x10180, 0x10C00);              // → name
        Encoding.ASCII.GetBytes("test_entry").CopyTo(Mem.TestMap!, (int)(0x10C00 - Base));
        W32(0x10188, 1234);                 // CodeId
        W32(0x1019C, 5);                    // StartOff
        W32(0x101A0, 2);                    // Locals
        W32(0x101A4, 1);                    // Argc
    }

    [Fact]
    public void ValidNode_PassesAndFieldsDecode()
    {
        PlantValidNode();
        Assert.True(NodeIndex.TryValidate(0x10100, out string name, out var info));
        Assert.Equal("test_entry", name);
        Assert.Equal(0x10100ul, info.Node);
        Assert.Equal(0x10800ul, info.Record);
        Assert.Equal(1234u, info.CodeId);
        Assert.Equal(5u, info.StartOff);
        Assert.Equal(2u, info.Locals);
        Assert.Equal(1u, info.Argc);
        Assert.Equal(0x11000ul, info.BufPtr);
        Assert.Equal(712u, info.BufLen);
    }

    [Fact]
    public void BadSentinel_Rejected()
    {
        PlantValidNode();
        W32(0x10164, 0x00FFFFFE);
        Assert.False(NodeIndex.TryValidate(0x10100, out _, out _));
    }

    [Fact]
    public void BadExecVtable_Rejected()
    {
        PlantValidNode();
        W64(0x10800, 0xDEAD);
        Assert.False(NodeIndex.TryValidate(0x10100, out _, out _));
    }

    [Fact]
    public void UnreadableName_Rejected()
    {
        PlantValidNode();
        W64(0x10180, 0x90000);   // TestMap 之外 → 守卫读返回 ""
        Assert.False(NodeIndex.TryValidate(0x10100, out _, out _));
    }

    [Fact]
    public void Build_OverTestMap_FindsPlantedNode()
    {
        PlantValidNode();
        Assert.Equal(1, NodeIndex.Build());
        Assert.True(NodeIndex.TryGet("test_entry", out var info));
        Assert.Equal(1234u, info.CodeId);
    }

    [Fact]
    public void Build_WithZeroSignature_ShortCircuits()
    {
        AgentState.NodeSigFn = 0;   // 未初始化常量：必须 0 命中而不是扫全零 qword 爆量
        Assert.Equal(0, NodeIndex.Build());
    }
}
