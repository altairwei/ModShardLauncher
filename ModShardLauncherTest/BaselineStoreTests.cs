using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class BaselineStoreTests : IDisposable
{
    readonly List<string> tmps = new();

    string NewTmp()
    {
        string p = Path.Combine(Path.GetTempPath(), "msl_base_" + Guid.NewGuid().ToString("N") + ".win");
        File.Copy(TestData.VanillaPath, p);
        tmps.Add(p);
        return p;
    }

    static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return UndertaleIO.Read(fs, w => { });   // 读完即释放——追加哈希区分字节需要文件未占用
    }

    static void AppendByte(string path, byte b)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
        fs.WriteByte(b);   // 尾部垃圾字节：解析器按 chunk 目录读，忽略末尾——只改变文件哈希
    }

    [Fact]
    public void RegisterThenLock_SameHash_Locks()
    {
        string p = NewTmp();
        var rec = BaselineStore.Register(Load(p), p, new List<LiveTextureEntry>());
        Assert.NotNull(BaselineStore.LockBaseline(rec.Hash, out bool isNew, out _));
        Assert.True(isNew);
        Assert.Same(rec, BaselineStore.BootBaseline);
        // 同会话再锁：isNewGameSession=false
        Assert.NotNull(BaselineStore.LockBaseline(rec.Hash, out isNew, out _));
        Assert.False(isNew);
    }

    [Fact]
    public void Lock_GameBootedOnOlderCompile_StillFound()
    {
        // 常规 dev 循环：v1 起游戏，之后编译 v2/v3——v1 必须仍在窗口内可锁。
        // 先 Load 再给文件追加区分字节（解析器不容忍尾部垃圾——IOException: Reading out of bounds，
        // 实测；所以「加载的是干净副本，注册时文件已变化」——与真实 dev 流程同形态），哈希互异。
        string p1 = NewTmp();
        var r1 = BaselineStore.Register(Load(p1), p1, new List<LiveTextureEntry>());
        string p2 = NewTmp();
        var d2 = Load(p2);
        AppendByte(p2, 1);
        BaselineStore.Register(d2, p2, new List<LiveTextureEntry>());
        string p3 = NewTmp();
        var d3 = Load(p3);
        AppendByte(p3, 2);
        BaselineStore.Register(d3, p3, new List<LiveTextureEntry>());
        Assert.NotNull(BaselineStore.LockBaseline(r1.Hash, out bool isNew, out _));
        Assert.True(isNew);
        Assert.Same(r1, BaselineStore.BootBaseline);
    }

    [Fact]
    public void Lock_UnknownHash_RefusesWithAdvice()
    {
        string p = NewTmp();
        BaselineStore.Register(Load(p), p, new List<LiveTextureEntry>());
        Assert.Null(BaselineStore.LockBaseline("DEADBEEF", out _, out string reason));
        Assert.Contains("重启游戏", reason);
        Assert.Null(BaselineStore.BootBaseline);
    }

    [Fact]
    public void StrgIndex_MapsContentToIndex()
    {
        string p = NewTmp();
        var rec = BaselineStore.Register(Load(p), p, new List<LiveTextureEntry>());
        var map = rec.StrgIndex.Value;
        Assert.Equal(0, map[rec.Product.Strings[0].Content]);
        Assert.Equal(rec.Product.Strings.Count - 1,
            map[rec.Product.Strings[rec.Product.Strings.Count - 1].Content]);
    }

    public void Dispose()
    {
        BaselineStore.Reset();
        foreach (var p in tmps) try { File.Delete(p); } catch { }
    }
}
