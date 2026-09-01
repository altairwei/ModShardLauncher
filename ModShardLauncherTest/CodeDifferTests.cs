using ModShardLauncher;
using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

[Trait("Category", "RealData")]
[Collection("vanilla")]
public class CodeDifferTests : IDisposable
{
    readonly VanillaFixture fixture;
    readonly UndertaleData? savedData;
    public CodeDifferTests(VanillaFixture fixture)
    {
        this.fixture = fixture;
        savedData = DataLoader.data;
    }
    public void Dispose() { if (savedData != null) DataLoader.data = savedData; }

    const string TargetName = "gml_GlobalScript_scr_unitRenderDrawSprite";

    [Fact]
    public void Diff_SameFileTwice_ReturnsNoChanges()
    {
        var product = VanillaFixture.LoadFreshVanilla();
        Assert.Empty(CodeDiffer.Diff(fixture.Vanilla, product));
    }

    [Fact]
    public void Diff_ReplacedEntry_FlagsExactlyThatEntry()
    {
        var product = VanillaFixture.LoadFreshVanilla();
        product.Code.First(c => c.Name.Content == TargetName).ReplaceGML(@"
draw_sprite(sprite_index, 0, x, y);
show_debug_message(""msl_p0_probe"");
", product);

        var changed = CodeDiffer.Diff(fixture.Vanilla, product);
        var entry = Assert.Single(changed);
        Assert.Equal(TargetName, entry.Name);
    }

    [Fact]
    public void Diff_ReplacedEntry_DoesNotMutateBaseline()
    {
        var baselineEntry = fixture.Vanilla.Code.First(c => c.Name.Content == TargetName);
        int before = baselineEntry.Instructions.Count;
        var product = VanillaFixture.LoadFreshVanilla();
        product.Code.First(c => c.Name.Content == TargetName).ReplaceGML("x = 1;", product);
        CodeDiffer.Diff(fixture.Vanilla, product);
        Assert.Equal(before, baselineEntry.Instructions.Count);
    }

    /// <summary>#16b bug #3 + product-only 发现：新脚本（function 声明 → 根+gml_Script_ 子，
    /// TW mod 源同款形态）必须在 changed 里产出<b>且只产出根</b>——子条目是父 blob 的组成
    /// 部分（S2④：父子共享 buffer，SwapCode 粒度=父 buffer 整块），子 op 会在 agent 侧被
    /// StartOff≠0 拒 → 整批失败。旧 Diff 只遍历 baseline 侧 → product-only 从不产出 →
    /// 槽/壳/房间路由全是死代码（计划 2709 行预期落空，smoke 项 2「行为可见」必败）。</summary>
    [Fact]
    public void Diff_NewWrapperScript_YieldsProductOnlyRoot_NotChild()
    {
        var product = VanillaFixture.LoadFreshVanilla();
        DataLoader.data = product;   // Msl.* 原语硬绑定 ModLoader.Data
        Msl.AddFunction("function scr_brand_new() { return 42; }", "scr_brand_new");

        var changed = CodeDiffer.Diff(fixture.Vanilla, product);
        var entry = Assert.Single(changed);
        Assert.Equal("scr_brand_new", entry.Name);
        Assert.Null(entry.Baseline);   // product-only → BuildSwapOp 槽路由
        Assert.NotNull(entry.Product);
        // wrapper 形态在场（BuildSwapOp 的槽载荷前提）
        Assert.NotEmpty(entry.Product.ChildEntries);
    }
}
