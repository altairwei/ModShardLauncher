using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

[Trait("Category", "RealData")]
[Collection("vanilla")]
public class Gen8GuardTests
{
    readonly VanillaFixture fixture;
    public Gen8GuardTests(VanillaFixture fixture) => this.fixture = fixture;

    [Fact]
    public void VersionOf_FormatsGen8()
    {
        var v = Gen8Guard.VersionOf(fixture.Vanilla);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+ bc\d+$", v);
    }

    [Fact]
    public void Check_SameFileTwice_Passes()
    {
        var product = VanillaFixture.LoadFreshVanilla();
        var (b, p) = Gen8Guard.Check(fixture.Vanilla, product);
        Assert.Equal(b, p);
    }
}
