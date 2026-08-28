using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class AssetDifferTests
{
    static UndertaleData Load() => UndertaleIO.Read(
        new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read), w => { });

    [Fact]
    public void IdenticalData_NoChanges()
    {
        var b = Load(); var p = Load();
        var diff = AssetDiffer.Diff(b, p, new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Empty(diff.Sprites);
        Assert.False(diff.SoundFontPathContentChanged);
    }

    [Fact]
    public void FieldEdit_Detected()
    {
        var b = Load(); var p = Load();
        p.Sprites[100].OriginX += 3;
        var diff = AssetDiffer.Diff(b, p, new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        var c = Assert.Single(diff.Sprites);
        Assert.True(c.FieldsChanged);
        Assert.False(c.FramesChanged);
        Assert.Equal(100, c.ProductIndex);
        Assert.Equal(100, c.BaselineIndex);
    }

    [Fact]
    public void NewSprite_IsNew_WithScanEntry()
    {
        var b = Load(); var p = Load();
        var s = new UndertaleSprite { Name = p.Strings.MakeString("s_test_new") };
        p.Sprites.Add(s);
        var cur = new List<LiveTextureEntry> { new() { SpriteName = "s_test_new", Frame = 0, ModName = "m", FileName = "s_test_new_0.png", Sha256 = "AA" } };
        var diff = AssetDiffer.Diff(b, p, cur, new List<LiveTextureEntry>());
        var c = Assert.Single(diff.Sprites);
        Assert.True(c.IsNew);
        Assert.Equal(p.Sprites.Count - 1, c.ProductIndex);
    }

    [Fact]
    public void FrameHashChange_Detected_OnlyWhenTouched()
    {
        var b = Load(); var p = Load();
        string name = p.Sprites[50].Name.Content;
        var boot = new List<LiveTextureEntry> { new() { SpriteName = name, Frame = 0, ModName = "m", FileName = "x.png", Sha256 = "AA" } };
        var cur = new List<LiveTextureEntry> { new() { SpriteName = name, Frame = 0, ModName = "m", FileName = "x.png", Sha256 = "BB" } };
        var diff = AssetDiffer.Diff(b, p, cur, boot);
        var c = Assert.Single(diff.Sprites);
        Assert.True(c.FramesChanged);
        // 两侧 scan 都为空时，同名同字段精灵不得误报帧变化
        var diff2 = AssetDiffer.Diff(b, p, new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Empty(diff2.Sprites);
    }

    [Fact]
    public void NewSound_CountChange_FlagsRestart()
    {
        var b = Load(); var p = Load();
        p.Sounds.Add(new UndertaleSound { Name = p.Strings.MakeString("snd_new") });
        var diff = AssetDiffer.Diff(b, p, new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(diff.SoundFontPathContentChanged);
        Assert.Contains(diff.NewAssets, a => a.Kind == AssetKind.Sound && a.Name == "snd_new");
    }
}
