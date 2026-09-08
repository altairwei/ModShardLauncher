using ModShardLauncher.HotReload;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 7] 指纹/绊线：mod 集合顺序敏感比对 + vanilla 文件 mtime/size 快查、
/// 不符再 MD5 复核。生产路径走 ModInfos.Instance（单测为 null → 可选列表参注入）。
/// 操纵 DataLoader.dataPath 共享静态 + 用 vanilla 集合同池——进 [Collection("vanilla")] 串行。</summary>
[Collection("vanilla")]
public class FingerprintTripwireTests : IDisposable
{
    readonly string savedDataPath = DataLoader.dataPath;

    public FingerprintTripwireTests()
    {
        ModFingerprint.Reset();
        VanillaTripwire.Reset();
    }

    public void Dispose()
    {
        DataLoader.dataPath = savedDataPath;
        ModFingerprint.Reset();
        VanillaTripwire.Reset();
    }

    [Fact]
    public void ModFingerprint_BirthThenVerify_MismatchWhenSetChanges()
    {
        // ModInfos.Instance 在单测为 null → Capture() 返回 ""。为可测，Capture 接受可选列表参：
        // public static string Capture(IEnumerable<string>? enabled = null)（Instance null 且未传 → ""）
        ModFingerprint.RecordBirth(new[] { "A", "B" });
        Assert.True(ModFingerprint.Verify(new[] { "A", "B" }));
        Assert.False(ModFingerprint.Verify(new[] { "A", "C" }));
        Assert.False(ModFingerprint.Verify(new[] { "B", "A" }));   // 顺序也算
    }

    [Fact]
    public void VanillaTripwire_FileChanged_VerifyFails()
    {
        string p = Path.Combine(Path.GetTempPath(), "msl_trip_" + Guid.NewGuid().ToString("N") + ".win");
        File.Copy(TestData.VanillaPath, p);
        try
        {
            DataLoader.dataPath = p;
            VanillaTripwire.Record();
            Assert.True(VanillaTripwire.Verify(out _));
            File.AppendAllText(p, "x");   // 尺寸变 → 快查就拒
            Assert.False(VanillaTripwire.Verify(out string reason));
            Assert.Contains("data.win", reason);
        }
        finally { File.Delete(p); }
    }
}
