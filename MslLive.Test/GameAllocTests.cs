using System;
using System.Text;
using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>fix-loop #27：游戏调试堆分配器定位 + AllocRW 委托。退出 AV 根因 = 换入缓冲
/// 走裸 VirtualAlloc 无调试堆头（+0x0c=0xdeadc0de/+0x10=0xbaadb00b @buf-0x20），退出遍历
/// free(record+0x18 buf) 经 0x140502cbc thunk 进 debug free 读头必 AV（12:08/13:50/16:36
/// 三例同形）。修法 = 定位游戏分配函数（AOB = 两魔术字写入序列，.text 全库唯一；.pdata
/// RUNTIME_FUNCTION 二分定函数起点，不猜 prologue 不硬编码 VA），AllocRW 生产分支改走它。
/// 这里用合成 mini-PE 钉字节级行为 + 委托缝钉 fail-closed。</summary>
public class GameAllocTests : IDisposable
{
    const ulong Base = 0x140_0000;
    // mini-PE 布局（RVA）：DOS(e_lfanew@+0x3C→0x40) + "PE\0\0"@0x40 + COFF@0x44(nsec=2,
    // optSize=0xF0) + 节表@0x148(.text/.pdata) + .text 里的假函数 + .pdata 一条 RUNTIME_FUNCTION
    const uint FnBegin = 0x1100, FnEnd = 0x1160;   // 假函数边界
    const uint AobRva = FnBegin + 0x20;            // AOB 埋函数体内 +0x20

    /// <summary>mov [rax+0xc],0xdeadc0de ; mov [rax+0x10],0xbaadb00b（分配器头写序列）。</summary>
    static readonly byte[] Aob =
    {
        0xC7, 0x40, 0x0C, 0xDE, 0xC0, 0xAD, 0xDE,
        0xC7, 0x40, 0x10, 0x0B, 0xB0, 0xAD, 0xBA,
    };

    public GameAllocTests()
    {
        Mem.TestMap = new byte[0x4000];
        Mem.TestBase = Base;
        Mem.GameAllocOverride = null;
        BuildImage(textSz: 0x800, textVa: 0x1000, pdataVa: 0x2000,
            fnBegin: FnBegin, fnEnd: FnEnd, aobRva: AobRva, uniqueAob: true, aob2Rva: 0);
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        Mem.GameAllocOverride = null;
    }

    void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));

    /// <summary>种一张合成 mini-PE。参数化以覆盖分块扫描接缝（textSz &gt; 0x40000 的跨界形态）。</summary>
    void BuildImage(uint textSz, uint textVa, uint pdataVa,
        uint fnBegin, uint fnEnd, uint aobRva, bool uniqueAob, uint aob2Rva)
    {
        var m = Mem.TestMap!;
        Array.Clear(m, 0, m.Length);
        W32(Base + 0x3C, 0x40);                                   // e_lfanew
        m[0x40] = (byte)'P'; m[0x41] = (byte)'E';                 // PE\0\0
        W32(Base + 0x44, 0x8664u | (2u << 16));                   // Machine=x64 | nsec=2
        W32(Base + 0x54, 0xF0);                                   // COFF+16: optSize=0xF0
        Encoding.ASCII.GetBytes(".text\0").CopyTo(m, 0x148);      // 节表条目 0
        W32(Base + 0x150, textSz);                                //   VirtualSize
        W32(Base + 0x154, textVa);                                //   VirtualAddress
        Encoding.ASCII.GetBytes(".pdata\0").CopyTo(m, 0x170);     // 节表条目 1
        W32(Base + 0x178, 12);                                    //   VirtualSize（1 条）
        W32(Base + 0x17C, pdataVa);                               //   VirtualAddress
        // .text：前导 CC 填充，函数体 0x90，AOB 埋函数内（先 .text 后 .pdata：不重叠时顺序无关）
        for (uint a = textVa; a < fnBegin; a++) m[(int)a] = 0xCC;
        for (uint a = fnBegin; a < fnEnd; a++) m[(int)a] = 0x90;
        Aob.CopyTo(m, (int)aobRva);
        if (!uniqueAob) Aob.CopyTo(m, (int)aob2Rva);
        // .pdata：一条 RUNTIME_FUNCTION（Begin, End, Unwind）
        W32(Base + pdataVa, fnBegin);
        W32(Base + pdataVa + 4, fnEnd);
        W32(Base + pdataVa + 8, 0x1400);
    }

    [Fact]
    public void Locate_FindsFunctionStart()
    {
        Assert.Equal(Base + FnBegin, Mem.LocateGameAlloc(Base));
    }

    [Fact]
    public void Locate_AobTwice_Fails()
    {
        BuildImage(textSz: 0x800, textVa: 0x1000, pdataVa: 0x2000,
            FnBegin, FnEnd, AobRva, uniqueAob: false, aob2Rva: 0x1700);
        Assert.Equal(0ul, Mem.LocateGameAlloc(Base));
    }

    [Fact]
    public void Locate_NoAob_Fails()
    {
        Array.Clear(Mem.TestMap!, (int)AobRva, Aob.Length);
        Assert.Equal(0ul, Mem.LocateGameAlloc(Base));
    }

    [Fact]
    public void Locate_NoPdataCoverage_Fails()
    {
        // .pdata 条目挪走：AOB 命中不在任何函数内
        W32(Base + 0x2000, FnBegin + 0x1000);
        W32(Base + 0x2000 + 4, FnEnd + 0x1000);
        Assert.Equal(0ul, Mem.LocateGameAlloc(Base));
    }

    [Fact]
    public void Locate_FunctionTooLarge_Fails()
    {
        W32(Base + 0x2000 + 4, FnBegin + 0x2001);   // End-Begin > 0x2000 → 拒
        Assert.Equal(0ul, Mem.LocateGameAlloc(Base));
    }

    [Fact]
    public void Locate_StraddlesChunkBoundary()
    {
        // .text 拉过 0x40000：AOB 起点 0x40FF8 落在第一块的 owned 起点（<0x41000）但末字节
        // 越过块界（0x41005）——分块读重叠 13 字节、起点归属不重叠，两侧都要对
        Mem.TestMap = new byte[0x43000];
        BuildImage(textSz: 0x41000, textVa: 0x1000, pdataVa: 0x42100,
            fnBegin: 0x40F00, fnEnd: 0x41080, aobRva: 0x40FF8, uniqueAob: true, aob2Rva: 0);
        Assert.Equal(Base + 0x40F00, Mem.LocateGameAlloc(Base));
    }

    // ---- AllocRW 委托缝（TestMap=null 的生产分支由托管假分配器接管）----

    [Fact]
    public void AllocRW_Override_ReceivesBytesAndPropagates()
    {
        Mem.TestMap = null;
        byte[] payload = { 1, 2, 3, 4, 5 };
        byte[]? seen = null;
        Mem.GameAllocOverride = d => { seen = d; return 0xAAA0; };
        Assert.Equal(0xAAA0ul, Mem.AllocRW(payload));
        Assert.Equal(payload, seen);
    }

    [Fact]
    public void AllocRW_OverrideZero_ReturnsZero()
    {
        Mem.TestMap = null;
        Mem.GameAllocOverride = _ => 0;
        Assert.Equal(0ul, Mem.AllocRW(new byte[] { 9, 9 }));
    }

    [Fact]
    public void AllocRW_TestMap_BehavesAsBefore()
    {
        // 回归锚：合成内存路径不受 #27 影响（既有 trampoline/proof 测试全靠它）
        byte[] payload = { 7, 7, 7 };
        ulong p = Mem.AllocRW(payload);
        Assert.NotEqual(0ul, p);
        Assert.Equal(payload, Mem.TestAllocs[p]);
    }
}
