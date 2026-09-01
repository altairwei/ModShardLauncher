using ModShardLauncher.HotReload;
using MslLive.Shared;
using Xunit;

namespace ModShardLauncherTest;

public class LoaderGenTests
{
    static SessionState NewState()
    {
        var s = new SessionState(new LiveQuotas(), new[]
        { (100, "o_button"), (101, "o_button"), (102, ""), (103, "") });
        s.SetBlanks(new BlanksMsg { SpriteFirst = 17248, SpriteCount = 64, PathFirst = 32, PathCount = 16 });
        return s;
    }

    [Fact]
    public void AllocateScript_StableAcrossCalls()
    {
        var s = NewState();
        Assert.Equal("msl_slot_0", s.AllocateScript("scr_a"));
        Assert.Equal("msl_slot_0", s.AllocateScript("scr_a"));   // 同名同槽
        Assert.Equal("msl_slot_1", s.AllocateScript("scr_b"));
    }

    [Fact]
    public void AllocateScript_PoolExhaustion_Throws()
    {
        var s = new SessionState(new LiveQuotas { ScriptSlots = 2 }, System.Array.Empty<(int, string)>());
        s.AllocateScript("a"); s.AllocateScript("b");
        Assert.Throws<PoolExhaustedException>(() => s.AllocateScript("c"));
    }

    [Fact]
    public void AllocateShell_ParentBucketMatching()
    {
        var s = NewState();
        Assert.Equal(100, s.AllocateShell("o_new_btn", "o_button"));
        Assert.Equal(102, s.AllocateShell("o_new_plain", null));
        Assert.Throws<PoolExhaustedException>(() => s.AllocateShell("o_new_menu", "o_menuParent")); // 无桶
    }

    [Fact]
    public void SpriteLoader_EmitsThreeStepWithIndexPath()
    {
        var c = new SpriteChange { Name = "s_x" };
        var strip = new StripInfo { RelPath = "mods/_live/res/17248.png", Frames = 3, OriginX = 4, OriginY = 5 };
        string gml = LoaderGen.SpriteLoader(c, strip, 17248);
        // 路径拼接用裸数字而非 string(字面量)（fix-loop #16 fold-probe：string(字面量) 形态
        // 被编译器常量折叠成非 boot 整路径字面量 → agent 拒；裸数字不折叠且局部数仍 1）
        Assert.Contains("sprite_add(\"mods/_live/res/\" + 17248 + \".png\", 3, false, false, 4, 5)", gml);
        Assert.Contains("sprite_assign(17248, _t);", gml);
        Assert.Contains("sprite_delete(_t);", gml);
    }

    [Fact]
    public void ShellConfig_WithSprite_EmitsObjectSetSprite()
    {
        string gml = LoaderGen.ShellConfig("o_new_btn", 100, 17248);
        Assert.Contains("object_set_sprite(100, 17248);", gml);
        Assert.Equal("", LoaderGen.ShellConfig("o_new_plain", 102, null));
    }
}
