using System.Text.Json;
using ModShardLauncher.HotReload;
using MslLive.Shared;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

[Trait("Category", "RealData")]
[Collection("vanilla")]
public class RefsExtractorTests
{
    readonly VanillaFixture fixture;
    public RefsExtractorTests(VanillaFixture fixture) => this.fixture = fixture;

    const string TargetName = "gml_GlobalScript_scr_unitRenderDrawSprite";

    [Fact]
    public void Extract_ReplacedEntry_CarriesNameTables_AndRoundTripsJson()
    {
        var product = VanillaFixture.LoadFreshVanilla();
        product.Code.First(c => c.Name.Content == TargetName).ReplaceGML(@"
draw_sprite(sprite_index, 0, x, y);
show_debug_message(""msl_p0_probe"");
", product);
        var code = product.Code.First(c => c.Name.Content == TargetName);

        var payload = RefsExtractor.Extract(code, product, AssetRanges.Empty, new AssetKindResolver());

        Assert.Equal(TargetName, payload.Entry);
        Assert.NotEmpty(payload.Instructions);
        Assert.Contains("draw_sprite", payload.Functions);
        Assert.Contains("show_debug_message", payload.Functions);
        Assert.Contains("msl_p0_probe", payload.Strings);
        Assert.Contains("sprite_index", payload.Variables);
        Assert.Contains("x", payload.Variables);
        // 空资产区间 → 不产生任何标注
        Assert.Empty(payload.Assets);
        Assert.All(payload.Instructions, s => Assert.True(s.AssetKinds == null || s.AssetKinds.Count > 0));

        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<SwapCodePayload>(json);
        Assert.NotNull(back);
        Assert.Equal(payload.Instructions.Count, back!.Instructions.Count);
        Assert.Equal(payload.Strings, back.Strings);
        Assert.Equal(payload.Functions, back.Functions);
        Assert.Equal(payload.Variables, back.Variables);
    }

    [Fact]
    public void Extract_VanillaEntryWithScriptAsValuePush_CarriesPushedFunctionName()
    {
        foreach (var code in fixture.Vanilla.Code)
        {
            var hit = code.Instructions.FirstOrDefault(i => i.Value is UndertaleInstruction.Reference<UndertaleFunction>);
            if (hit == null) continue;
            var payload = RefsExtractor.Extract(code, fixture.Vanilla, AssetRanges.Empty, new AssetKindResolver());
            var name = ((UndertaleInstruction.Reference<UndertaleFunction>)hit.Value).Target.Name.Content;
            Assert.Contains(name, payload.Functions);
            return;
        }
        Assert.Fail("no push-carried function reference found in vanilla (probe counted 8547 — unexpected)");
    }

    [Fact]
    public void Extract_LiteralOf_ReadsPushiAsLong()
    {
        var code = fixture.Vanilla.Code.First(c => c.Name.Content == TargetName);
        var pushi = code.Instructions.First(i => i.Kind == UndertaleInstruction.Opcode.PushI);
        long? lit = RefsExtractor.LiteralOf(pushi);
        Assert.NotNull(lit);
        Assert.Equal((short)pushi.Value, lit!.Value);
    }

    [Fact]
    public void Extract_PushEBreakEInt16Literals_CarryValueInLow16()
    {
        // fix-loop #14：GMS 2.3 的 int16 字面量有三种指令形态——pushi.e（Low16 已取 Value）、
        // push.e（布尔物化舞步 push.e 1）、break.e（数组边界标记 break.e -5）。后两者的字面量
        // 同样住在 inst.Value，Low16 必须重编码为该值（真机 golden：01 00 0F C0 / FB FF 0F FF）；
        // 落到 SwapExtra（=0x00）会把热交换字节流在这些指令上清零（真机 20/35 编码自证失败）。
        UndertaleCode? pushEntry = null, breakEntry = null;
        foreach (var code in fixture.Vanilla.Code)
        {
            foreach (var inst in code.Instructions)
            {
                if (inst.Value is not short sv || sv == 0) continue;
                if (inst.Kind == UndertaleInstruction.Opcode.Push && inst.Type1 == UndertaleInstruction.DataType.Int16)
                    pushEntry ??= code;
                else if (inst.Kind == UndertaleInstruction.Opcode.Break && inst.Type1 == UndertaleInstruction.DataType.Int16)
                    breakEntry ??= code;
            }
            if (pushEntry != null && breakEntry != null) break;
        }
        // probe：#14 audit 实测全库 6,601 entry 含此类指令（11,520 条非零），不可能扫不到
        Assert.NotNull(pushEntry);
        Assert.NotNull(breakEntry);
        AssertAllInt16LiteralsCarryValue(pushEntry!);
        AssertAllInt16LiteralsCarryValue(breakEntry!);

        void AssertAllInt16LiteralsCarryValue(UndertaleCode code)
        {
            var payload = RefsExtractor.Extract(code, fixture.Vanilla, AssetRanges.Empty, new AssetKindResolver());
            Assert.Equal(code.Instructions.Count, payload.Instructions.Count); // sem 与文件指令索引一一对应
            bool any = false;
            for (int i = 0; i < code.Instructions.Count; i++)
            {
                var inst = code.Instructions[i];
                if (inst.Kind is not (UndertaleInstruction.Opcode.Push or UndertaleInstruction.Opcode.Break)) continue;
                if (inst.Type1 != UndertaleInstruction.DataType.Int16) continue;
                if (inst.Value is not short sv) continue;
                any = true;
                Assert.Equal((ushort)sv, payload.Instructions[i].Low16);
            }
            Assert.True(any); // 扫描命中的 entry 必然含有目标指令
        }
    }

    [Fact]
    public void Extract_DupCallVLow16_CarryExtraAndComparisonKind()
    {
        // fix-loop #14 完备性验证（wordroundtrip 全库 34,422 entry 逐指令对账）扫出的新残差：
        // SingleType 指令的 low16 不在 SwapExtra——dup 的计数在 Extra(b0)，特型 dup
        // （swap 舞步）的第二个字节在 ComparisonKind(b1)，callv 的 argc 在 Extra(b0)。
        // classic UTML 0.6.1.0 反射实证：file=01 88 05 86 → Extra=0x01, CmpKind=0x88；
        // file=01 00 05 99 → Extra=0x01；file=04 00 02 86 → Extra=0x04。
        // 默认臂读 SwapExtra（classic 里恒 0）会把热交换字节流的这些 low16 清零——
        // VM 正是从 low16 读 dup 计数与 callv argc，清零即语义损坏
        // （blast radius：全库 28 条非零 dup + 25 条非零 callv，含 scr_skill_conditions
        // 等核心脚本；wordroundtrip WORD DIFF 实测 file=04.. enc=00..）。
        int hits = 0;
        foreach (var code in fixture.Vanilla.Code)
        {
            bool hasTarget = code.Instructions.Any(i =>
                i.Kind is UndertaleInstruction.Opcode.Dup or UndertaleInstruction.Opcode.CallV
                && (i.Extra != 0 || i.ComparisonKind != 0));
            if (!hasTarget) continue;
            var payload = RefsExtractor.Extract(code, fixture.Vanilla, AssetRanges.Empty, new AssetKindResolver());
            Assert.Equal(code.Instructions.Count, payload.Instructions.Count); // sem 与文件指令索引一一对应
            for (int i = 0; i < code.Instructions.Count; i++)
            {
                var inst = code.Instructions[i];
                if (inst.Kind is not (UndertaleInstruction.Opcode.Dup or UndertaleInstruction.Opcode.CallV)) continue;
                ushort expected = (ushort)(inst.Extra | ((int)inst.ComparisonKind << 8));
                if (expected == 0) continue; // 零值 dup/callv 走默认臂结果相同，不具判别力
                hits++;
                Assert.Equal(expected, payload.Instructions[i].Low16);
            }
        }
        Assert.True(hits >= 50); // wordroundtrip 实测 28 dup + 25 callv 非零；扫全库必命中
    }
}
