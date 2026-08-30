using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>Registry 记录校验回归（Task 16 fix-loop #6，真机 apply=-1/report=-1 的根因）：
/// fn 区间校验 [0x140000000, 0x142000000) 的本意是抓"表基址读错/记录错位"的垃圾——但
/// Rescan 发生在 Function_Add 注册自家 managed thunk 之后，UCO thunk 地址天然在游戏模块外
/// （真机 0x7FFF1A4E2630），被误杀 → Bootstrap 中止 → 索引里没有 apply/report → -1，
/// 且 FAIL 累积进 AgentStatus 令 MSL 拒会话。自家地址经 OwnFn 报备后必须放行；
/// 未报备的模块外地址仍要拒（保住原校验目的——那是防垃圾的，不是防我们的）。</summary>
public class RegistryTests : IDisposable
{
    const ulong ModuleFn = 0x140123456;      // 游戏模块内：vanilla 记录的合法形态
    const ulong ThunkFn = 0x7FFF1A4E2630;    // UCO thunk：模块外——我们自己的（真机同款地址形态）

    const int Count = 1001;                  // 过 Bootstrap 的 count>=1000 表规模守卫
    const ulong SlotBase = 0x10000000;       // 合成地址空间基址
    const int BasePtrOff = 0;                // *RegBasePtrVa → 表基址
    const int CountOff = 8;                  // *RegCountVa → 记录数
    const int TableOff = 0x1000;             // 记录表（0x50 步进，name-first）

    readonly byte[]? savedMap;
    readonly ulong savedBase;
    readonly ulong savedBasePtrVa, savedCountVa;
    readonly int savedIdx1, savedIdx2;

    public RegistryTests()
    {
        savedMap = Mem.TestMap; savedBase = Mem.TestBase;
        savedBasePtrVa = AgentState.RegBasePtrVa; savedCountVa = AgentState.RegCountVa;
        savedIdx1 = AgentState.RegAnchorIdx1; savedIdx2 = AgentState.RegAnchorIdx2;

        AgentState.ResetForTest();
        Registry.ResetForTest();
        BuildTable();
    }

    void BuildTable()
    {
        // [0]sprite_exists [1]object_get_name（锚，AgentState.RegAnchorName1/2 是 const）
        // [2..998]filler（模块内 fn）[999]msl_live_apply [1000]msl_live_report（模块外 thunk）
        var map = new byte[TableOff + Count * 0x50];
        Mem.TestBase = SlotBase;
        Mem.TestMap = map;
        BitConverter.GetBytes((ulong)(SlotBase + (ulong)TableOff)).CopyTo(map, BasePtrOff);
        BitConverter.GetBytes((uint)Count).CopyTo(map, CountOff);
        PutRecord(map, TableOff + 0 * 0x50, AgentState.RegAnchorName1, ModuleFn);
        PutRecord(map, TableOff + 1 * 0x50, AgentState.RegAnchorName2, ModuleFn);
        for (int i = 2; i <= Count - 3; i++)
            PutRecord(map, TableOff + i * 0x50, $"gml_builtin_{i}", ModuleFn);
        PutRecord(map, TableOff + (Count - 2) * 0x50, "msl_live_apply", ThunkFn);
        PutRecord(map, TableOff + (Count - 1) * 0x50, "msl_live_report", ThunkFn);

        AgentState.RegBasePtrVa = SlotBase + (ulong)BasePtrOff;
        AgentState.RegCountVa = SlotBase + (ulong)CountOff;
        AgentState.RegAnchorIdx1 = 0;
        AgentState.RegAnchorIdx2 = 1;
    }

    static void PutRecord(byte[] map, int off, string name, ulong fn)
    {
        var nb = Encoding.ASCII.GetBytes(name);
        Array.Copy(nb, 0, map, off, nb.Length);                       // +0x00 内联名（NUL 垫满）
        BitConverter.GetBytes(fn).CopyTo(map, off + 0x40);            // +0x40 funcptr
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(map, off + 0x4C);   // +0x4C 尾标记
    }

    [Fact]
    public void OwnedThunkRecords_AreAcceptedByRescan()
    {
        Registry.OwnFn(ThunkFn);   // RegisterAll 在 Rescan 前的报备
        Assert.True(Registry.Bootstrap());
        Assert.Equal(Count, Registry.Count);
        Assert.Equal(Count - 2, Registry.IndexOf("msl_live_apply"));
        Assert.Equal(Count - 1, Registry.IndexOf("msl_live_report"));
        Assert.Equal("ok", AgentState.Status);   // 不再毒化 hello.AgentStatus
    }

    [Fact]
    public void UnownedOutOfModuleRecord_IsStillRejected()
    {
        // 未报备的模块外 fn = 表读错位的垃圾，照拒——校验的原始目的不许被修坏
        Assert.False(Registry.Bootstrap());
        Assert.Contains($"registry record {Count - 2} invalid", AgentState.Status);
        Assert.Equal(-1, Registry.IndexOf("msl_live_apply"));
    }

    public void Dispose()
    {
        Mem.TestMap = savedMap; Mem.TestBase = savedBase;
        AgentState.RegBasePtrVa = savedBasePtrVa; AgentState.RegCountVa = savedCountVa;
        AgentState.RegAnchorIdx1 = savedIdx1; AgentState.RegAnchorIdx2 = savedIdx2;
        Registry.ResetForTest();
    }
}
