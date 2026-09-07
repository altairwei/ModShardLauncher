using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 2] DataLoader.VanillaHash：LoadUmt 直驱真身（internal 缝），
/// 每次装载成功后 = 该文件 MD5 hex。直驱会覆写静态 DataLoader.data——必须挂 vanilla 集合串行。</summary>
[Collection("vanilla")]
public class VanillaHashTests : IDisposable
{
    readonly string? savedHash;
    readonly string? savedPath;

    public VanillaHashTests()
    {
        savedHash = DataLoader.VanillaHash;
        savedPath = DataLoader.dataPath;
    }

    public void Dispose()
    {
        DataLoader.VanillaHash = null;
        DataLoader.dataPath = savedPath!;
        DecompileCache.Clear();
        FastPushContext.ResetForTest();
    }

    [Fact]
    public void LoadUmt_SetsVanillaHash()
    {
        DataLoader.LoadUmt(TestData.VanillaPath);   // internal 直驱真身（hadWarnings 非语义面）
        Assert.NotNull(DataLoader.VanillaHash);
        Assert.Equal(32, DataLoader.VanillaHash!.Length);   // MD5 hex = 32 字符
        Assert.Matches("^[0-9A-F]{32}$", DataLoader.VanillaHash);
    }
}

/// <summary>[v2 Task 2] FastPushContext 轮界与脏集语义（纯静态，不拉 vanilla 图）。</summary>
public class FastPushContextTests : IDisposable
{
    public FastPushContextTests() { }
    public void Dispose() => FastPushContext.ResetForTest();

    [Fact]
    public void BeginEndPush_TracksFlagAndDirty()
    {
        FastPushContext.BeginPush();
        Assert.True(FastPushContext.InFastPush);
        FastPushContext.NoteDirty("e1");
        Assert.True(FastPushContext.IsDirty("e1"));
        FastPushContext.EndPush();
        Assert.False(FastPushContext.InFastPush);
        Assert.False(FastPushContext.IsDirty("e1"));
    }
}
