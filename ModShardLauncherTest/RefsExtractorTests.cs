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
}
