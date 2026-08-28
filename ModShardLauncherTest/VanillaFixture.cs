using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

[CollectionDefinition("vanilla")]
public sealed class VanillaCollection : ICollectionFixture<VanillaFixture> { }

public sealed class VanillaFixture
{
    public UndertaleData Vanilla { get; }

    public VanillaFixture()
    {
        Vanilla = LoadFreshVanilla(); // ~10–15 s，全 collection 只跑一次
    }

    /// <summary>FileShare.ReadWrite：游戏可能正在运行。加载 160MB 约需 10–15 s。</summary>
    public static UndertaleData LoadFreshVanilla()
    {
        using var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return UndertaleIO.Read(fs, w => { });
    }
}
