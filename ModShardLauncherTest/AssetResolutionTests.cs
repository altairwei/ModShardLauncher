using ModShardLauncher.HotReload;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>Ground truth：vanilla 里 draw_sprite 的字面量 arg0，其值必然索引一个真实精灵——
/// 用「把该字面量所在值域伪装成新增区间」的办法，验证 栈追踪→查表→定类→池取名 的完整链条。</summary>
[Trait("Category", "RealData")]
[Collection("vanilla")]
public class AssetResolutionTests
{
    readonly VanillaFixture fixture;
    public AssetResolutionTests(VanillaFixture fixture) => this.fixture = fixture;

    [Fact]
    public void VanillaDrawSpriteLiteralArg0_ResolvesToSprite_AndIndexesRealSprite()
    {
        var data = fixture.Vanilla;
        var resolver = new AssetKindResolver();

        foreach (var code in data.Code)
        {
            var tracker = new StackTracker(code.Instructions);
            for (int idx = 0; idx < code.Instructions.Count; idx++)
            {
                var inst = code.Instructions[idx];
                tracker.Before(inst);
                if (inst.Kind == UndertaleInstruction.Opcode.Call
                    && inst.Function?.Target?.Name?.Content == "draw_sprite"
                    && inst.ArgumentsCount == 4)
                {
                    // GMS VM 参数逆序压栈：arg0（sprite）在栈顶 = Top 列表末位
                    var producers = tracker.Top(4);
                    if (producers[^1] is int p
                        && RefsExtractor.LiteralOf(code.Instructions[p]) is long lit
                        && lit >= 0 && lit < data.Sprites.Count)
                    {
                        // 1 宽区间：只让这个字面量落入「新增区间」，避免同 entry 其他常量干扰
                        var oneWide = new AssetRanges((AssetKind.Sprite, (int)lit, (int)lit + 1));
                        var payload = RefsExtractor.Extract(code, data, oneWide, resolver);

                        var sem = payload.Instructions[p];
                        Assert.NotNull(sem.AssetKinds);
                        Assert.Contains("Sprite", sem.AssetKinds!);
                        var asset = Assert.Single(payload.Assets);
                        Assert.Equal("Sprite", asset.Kind);
                        Assert.Equal(lit, asset.Index);
                        Assert.Equal(data.Sprites[(int)lit].Name.Content, asset.Name);
                        return; // 一个真值样本即可证明链条
                    }
                }
                tracker.Apply(inst, idx);
            }
        }
        Assert.Fail("no literal-arg0 draw_sprite call found in vanilla (unexpected for 1.2M instructions)");
    }
}
