using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

[Trait("Category", "RealData")]
[Collection("vanilla")]
public class CodeDifferTests
{
    readonly VanillaFixture fixture;
    public CodeDifferTests(VanillaFixture fixture) => this.fixture = fixture;

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
}
