using System.Drawing;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

public class PngExtractorTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "msl_live_test_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteBlankPng_Creates1x1()
    {
        PngExtractor.WriteBlankPng(dir);
        using var bmp = new Bitmap(Path.Combine(dir, "_blank.png"));
        Assert.Equal(1, bmp.Width);
        Assert.Equal(1, bmp.Height);
    }

    [Fact]
    public void MissingScanEntry_Throws()
    {
        var c = new SpriteChange { Name = "s_ghost", Product = null! };
        var ex = Assert.Throws<PngExtractException>(() =>
            PngExtractor.EnsureSpritePngs(c, new List<LiveTextureEntry>(), new List<ModShardLauncher.ModFile>(), dir, 17248));
        Assert.Contains("s_ghost", ex.Message);
    }

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
