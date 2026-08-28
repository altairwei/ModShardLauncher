using MslLive.Shared;
using Xunit;

namespace ModShardLauncherTest;

public class BuiltinVarsTests
{
    [Fact]
    public void Anchors_MatchMeasuredIds()
    {
        // Task 11 实测锚点（findings-t11.md）：三个计划门锚点 + 五个交叉锚点
        Assert.Equal(26, BuiltinVars.Map["sprite_index"]);
        Assert.Equal(93, BuiltinVars.Map["argument0"]);
        Assert.Equal(14, BuiltinVars.Map["object_index"]);
        Assert.Equal(27, BuiltinVars.Map["image_index"]);
        Assert.Equal(36, BuiltinVars.Map["image_angle"]);
        Assert.Equal(37, BuiltinVars.Map["image_alpha"]);
        Assert.Equal(38, BuiltinVars.Map["image_blend"]);
        Assert.True(BuiltinVars.Map.Count > 100);
    }

    [Fact]
    public void Ids_AreContiguousFromZero()
    {
        // 注册序即 smallId：全表 218 条，ids 0..217 无洞（Task 11 全链锁定）
        var ids = BuiltinVars.Map.Values.OrderBy(x => x).ToArray();
        for (int i = 0; i < ids.Length; i++)
            Assert.Equal(i, ids[i]);
    }

    [Fact]
    public void TryGetId_UnknownName_Fails()
    {
        Assert.False(BuiltinVars.TryGetId("msl_no_such_var", out _));
    }
}
