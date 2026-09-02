using MslLive.Agent;
using Xunit;

namespace MslLive.Test;

/// <summary>TableBuilder = EnsureSpecialized（exe 0x140297B80/0x140297C99，#19 离线解剖钉版）
/// 的 agent 侧复刻：为换入 buffer 构建派发 handler 表（record+0x20）与 pcmap（record+0x28）。
/// 规则真源 = 反汇编：class=opcode&amp;0x1F；class 0x19 ∧ T1==2 按操作数分派（&lt;100000→原生
/// 0x14028B910、∈[100000,500000] 且 ≠499999→脚本 0x14028BA30、其余回通用槽）；class 0x07 ∧
/// b2==0x52 → 0x140282C80；class 0x05 ∧ T1==5 ∧ T2==5 ∧ low16==0xFFF9 ∧ bit30 → 0x140288160；
/// 其余 → 通用槽（运行时读 imageBase+0x6314F0，测试注入假槽）。长度表（T1 索引，imageBase+
/// 0x6314B0 实测值 {8,4,4,8,4,4,4,4,0×8}）+ bit30 扩展位——与 BcEncoder.ByteSize 同知识双源。
/// pcmap：起始单元写指令序号、内部单元 0xFFFFFFFF（pass memset 0xFF 后只写起点）、+1 零哨兵；
/// 表 = count+1 qword，末位零哨兵（pass calloc 语义）。</summary>
public class TableBuilderTests
{
    /// <summary>假通用槽：class → 0xA000+class（逐类可区分，错类即错值）。</summary>
    static ulong FakeSlot(int cls) => 0xA000 + (ulong)cls;

    static byte[] Words(params uint[] wordsAndOperands)
    {
        var b = new byte[wordsAndOperands.Length * 4];
        for (int i = 0; i < wordsAndOperands.Length; i++)
            BitConverter.GetBytes(wordsAndOperands[i]).CopyTo(b, i * 4);
        return b;
    }

    static ulong[] Qwords(byte[] table) =>
        Enumerable.Range(0, table.Length / 8).Select(i => BitConverter.ToUInt64(table, i * 8)).ToArray();

    static uint[] Dwords(byte[] map) =>
        Enumerable.Range(0, map.Length / 4).Select(i => BitConverter.ToUInt32(map, i * 4)).ToArray();

    // 指令字常量（BcEncoder 同款编码：op<<24 | T2<<20 | T1<<16 | low16）
    const uint ExitI = 0x9D020000;          // exit.i（T1=Int32）
    const uint PushVArg0 = 0xC005FFF7;      // push.v argument0（inst=-9）
    const uint CallV0 = 0xD9020000;         // call.v argc=0
    const uint CallV1 = 0xD9020001;         // call.v argc=1
    const uint PopzV = 0x9E050000;          // popz.v
    const uint PushI0 = 0x840F0000;         // pushi.e 0
    const uint ConvIV = 0x07520000;         // conv.i.v（b2=0x52）
    const uint RetV = 0x9C050000;           // ret.v

    [Fact]
    public void Build_ApplyTrampoline_TableAndPcmap()
    {
        // apply 28B：exit.i @0 / call.v #900 @4 / popz.v @12 / pushi.e 0 @16 / conv.i.v @20 / ret.v @24
        var code = Words(ExitI, CallV0, 900, PopzV, PushI0, ConvIV, RetV);
        var (table, map) = TableBuilder.Build(code, FakeSlot);

        Assert.Equal(new[]
        {
            FakeSlot(0x1D),                 // exit.i → 通用槽 0x1D
            TableBuilder.VaNativeCall,      // call.v 900（<100000）→ 原生立即数派发器
            FakeSlot(0x1E),                 // popz.v → 槽 0x1E
            FakeSlot(0x04),                 // pushi.e → 槽 0x04
            TableBuilder.VaConvIV,          // conv.i.v（b2=0x52）→ 专用
            FakeSlot(0x1C),                 // ret.v → 槽 0x1C
            0UL,                            // +1 零哨兵
        }, Qwords(table));

        // 7 单元：exit@0、call 占 1-2（起点 1，内部 -1）、popz@3、pushi@4、conv@5、ret@6、哨兵 0
        Assert.Equal(new[] { 0u, 1u, 0xFFFFFFFFu, 2u, 3u, 4u, 5u, 0u }, Dwords(map));
    }

    [Fact]
    public void Build_ReportTrampoline_TableAndPcmap()
    {
        // report 36B：exit.i @0 / push.v argument0 @4（8B）/ call.v #901 @12（8B）/ popz @20 / pushi @24 / conv @28 / ret @32
        var code = Words(ExitI, PushVArg0, 0xA000005D, CallV1, 901, PopzV, PushI0, ConvIV, RetV);
        var (table, map) = TableBuilder.Build(code, FakeSlot);

        Assert.Equal(new[]
        {
            FakeSlot(0x1D),
            FakeSlot(0x00),                 // push.v → 槽 0x00
            TableBuilder.VaNativeCall,      // call.v 901
            FakeSlot(0x1E),
            FakeSlot(0x04),
            TableBuilder.VaConvIV,
            FakeSlot(0x1C),
            0UL,
        }, Qwords(table));

        // 9 单元：exit@0、push.v 占 1-2、call 占 3-4、popz@5、pushi@6、conv@7、ret@8
        Assert.Equal(new[] { 0u, 1u, 0xFFFFFFFFu, 2u, 0xFFFFFFFFu, 3u, 4u, 5u, 6u, 0u }, Dwords(map));
    }

    [Theory]
    [InlineData(2535, TableBuilder.VaNativeCall)]       // 运行时原生注册槽
    [InlineData(99999, TableBuilder.VaNativeCall)]      // <100000 一律原生
    [InlineData(110135, TableBuilder.VaScriptCall)]     // 脚本空间
    [InlineData(500000, TableBuilder.VaScriptCall)]     // 上界含（pass 是 ja 非 jge）
    [InlineData(499999, 0)]                             // 0x7A11F 特例 → 回通用槽（0=断言走槽 0x19）
    [InlineData(500001, 0)]                             // 出窗 → 回通用槽
    [InlineData(-5, 0)]                                 // 负操作数（uint 回绕出窗）→ 通用槽
    public void Build_CallOperandRouting(int operand, ulong expected)
    {
        var code = Words(CallV0, (uint)operand);
        var (table, _) = TableBuilder.Build(code, FakeSlot);
        ulong want = expected != 0 ? expected : FakeSlot(0x19);
        Assert.Equal(want, Qwords(table)[0]);
        Assert.Equal(0UL, Qwords(table)[1]);            // 哨兵
    }

    [Fact]
    public void Build_CallV_NotT1Two_FallsToGenericSlot()
    {
        // callv 值调用（0x99，class 0x19 但 T1≠2——pass 不读「操作数」，回通用槽 0x19）
        var callv = Words(0x99050000);                  // T1=5 → 通用槽
        var (t, _) = TableBuilder.Build(callv, FakeSlot);
        Assert.Equal(FakeSlot(0x19), Qwords(t)[0]);
        Assert.Equal(0UL, Qwords(t)[1]);
    }

    [Fact]
    public void Build_ConvSpecial_OnlyWhenB2Is0x52()
    {
        var (ts, _) = TableBuilder.Build(Words(ConvIV), FakeSlot);
        Assert.Equal(TableBuilder.VaConvIV, Qwords(ts)[0]);

        var (tg, _) = TableBuilder.Build(Words(0x07050000), FakeSlot);   // conv b2=0x05 → 通用槽
        Assert.Equal(FakeSlot(0x07), Qwords(tg)[0]);
    }

    [Fact]
    public void Build_PopLocalSpecial()
    {
        // pop.v.v local：class 0x05 ∧ T1==5 ∧ T2==5 ∧ low16==0xFFF9 ∧ bit30（op 0x45 自带）→ 专用
        var (ts, _) = TableBuilder.Build(Words(0x4555FFF9, 0xDEADBEEF), FakeSlot);
        Assert.Equal(TableBuilder.VaPopLocalSpecial, Qwords(ts)[0]);

        // low16≠0xFFF9 → 通用槽 0x05
        var (tg, _) = TableBuilder.Build(Words(0x4555FFF6, 0xDEADBEEF), FakeSlot);
        Assert.Equal(FakeSlot(0x05), Qwords(tg)[0]);
    }

    [Fact]
    public void Build_PopzAllZeroTail_WrapperDummyShape()
    {
        // 68B dummy = 头 20B（B+5 / pushi / conv.i.v / ret.v / exit.i）+ 48B 零（op 0 槽 0，4B 步进）
        var head = Words(0xB6000005, PushI0, ConvIV, RetV, ExitI);
        var code = head.Concat(new byte[48]).ToArray();
        var (table, map) = TableBuilder.Build(code, FakeSlot);

        var want = new List<ulong>
        {
            FakeSlot(0x16),                 // b +5 → 槽 0x16
            FakeSlot(0x04),                 // pushi.e
            TableBuilder.VaConvIV,          // conv.i.v
            FakeSlot(0x1C),                 // ret.v
            FakeSlot(0x1D),                 // exit.i
        };
        want.AddRange(Enumerable.Repeat(FakeSlot(0x00), 12));   // 48B 零尾 = 12 条
        want.Add(0UL);
        Assert.Equal(want, Qwords(table));

        // 全 4B 指令：pcmap 逐单元递增无 -1，17 单元 + 哨兵
        Assert.Equal(Enumerable.Range(0, 17).Select(i => (uint)i).Append(0u), Dwords(map));
    }

    [Fact]
    public void Build_LengthTable_MatchesBcEncoderSizes()
    {
        // push.d（T1=0，12B）与 push.l（T1=3，12B）：pcmap 起点跳 3 单元
        var code = Words(0xC0000000, 0, 0, 0xC0030000, 0, 0, PopzV);
        var (_, map) = TableBuilder.Build(code, FakeSlot);
        Assert.Equal(new[] { 0u, 0xFFFFFFFFu, 0xFFFFFFFFu, 1u, 0xFFFFFFFFu, 0xFFFFFFFFu, 2u, 0u },
            Dwords(map));
    }

    [Fact]
    public void Build_NotFourAligned_Rejected()
    {
        Assert.Throws<TranslationRejectException>(() => TableBuilder.Build(new byte[6], FakeSlot));
    }

    [Fact]
    public void Build_Empty_Rejected()
    {
        Assert.Throws<TranslationRejectException>(() => TableBuilder.Build(Array.Empty<byte>(), FakeSlot));
    }
}
