using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

public class AssetKindResolverTests
{
    readonly AssetKindResolver resolver = new();

    [Fact]
    public void VariableTable_MapsKnownAssetVariables()
    {
        Assert.Equal(AssetKind.Sprite, resolver.KindForVariable("sprite_index"));
        Assert.Equal(AssetKind.Sprite, resolver.KindForVariable("mask_index"));
        Assert.Equal(AssetKind.Object, resolver.KindForVariable("object_index"));
        Assert.Equal(AssetKind.Room, resolver.KindForVariable("room"));
        Assert.Equal(AssetKind.Background, resolver.KindForVariable("background_index"));
        Assert.Equal(AssetKind.Path, resolver.KindForVariable("path_index"));
        Assert.Equal(AssetKind.Timeline, resolver.KindForVariable("timeline_index"));
        Assert.Null(resolver.KindForVariable("x"));
        Assert.Null(resolver.KindForVariable("hp"));
    }

    [Fact]
    public void FunctionTable_MapsCallArguments()
    {
        Assert.Equal(AssetKind.Sprite, resolver.KindForCallArg("draw_sprite", 0));
        Assert.Null(resolver.KindForCallArg("draw_sprite", 1));
        Assert.Equal(AssetKind.Object, resolver.KindForCallArg("instance_create", 2));
        Assert.Equal(AssetKind.Room, resolver.KindForCallArg("room_goto", 0));
        Assert.Equal(AssetKind.Sound, resolver.KindForCallArg("audio_play_sound", 0));
        Assert.Equal(AssetKind.Script, resolver.KindForCallArg("layer_script_end", 1));
        Assert.Null(resolver.KindForCallArg("scr_nonexistent_thing", 0));
        Assert.Null(resolver.KindForCallArg("draw_sprite", 99));
    }

    [Fact]
    public void PushEnv_IsAlwaysObject()
    {
        Assert.Equal(AssetKind.Object, resolver.KindForPushEnv());
    }

    [Fact]
    public void Resolver_RoomNext_IsRoom_BuiltinTable()
    {
        var r = new AssetKindResolver();
        Assert.Equal(AssetKind.Room, r.KindForVariable("roomNext"));
        Assert.Equal(AssetKind.Room, r.KindForVariable("roomPrevious"));
    }

    [Fact]
    public void Resolver_EmbeddedTable_MatchesSpikeKnowns()
    {
        var r = new AssetKindResolver();
        Assert.Equal(AssetKind.Sprite, r.KindForVariable("sprite_index"));
        Assert.Equal(AssetKind.Sprite, r.KindForCallArg("draw_sprite", 0));
        Assert.Equal(AssetKind.Object, r.KindForCallArg("instance_create", 2) ?? r.KindForCallArg("instance_create", 0));
        Assert.Null(r.KindForCallArg("scr_inventory_add_item", 0)); // 用户脚本无签名：保持 null（拒绝路径）
    }

    [Fact]
    public void IsBuiltinVariable_CoversTableAndRoomBuiltins()
    {
        Assert.True(resolver.IsBuiltinVariable("sprite_index"));
        Assert.True(resolver.IsBuiltinVariable("roomNext"));
        Assert.False(resolver.IsBuiltinVariable("_borderLeft"));
        Assert.False(resolver.IsBuiltinVariable("hp"));
    }
}
