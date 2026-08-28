using System;
using System.IO;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>DevModeInstaller 装/卸往返（全临时目录；System32 version.dll 与「游戏运行中」
/// 判据走注入缝）。钉：marker 存在才卸、运行中不卸、msllive-runtime 缺失即抛。</summary>
public class DevModeInstallerTests : IDisposable
{
    readonly string gameDir = Path.Combine(Path.GetTempPath(), "msl_game_" + Guid.NewGuid().ToString("N"));
    readonly string runtimeDir = Path.Combine(Path.GetTempPath(), "msl_rt_" + Guid.NewGuid().ToString("N"));
    readonly string fakeSysVer;
    readonly string origSysVer;

    public DevModeInstallerTests()
    {
        Directory.CreateDirectory(gameDir);
        // Build-MslLive.ps1 产物形态：msllive-runtime/{version.dll, msllive/<agent publish>}
        Directory.CreateDirectory(Path.Combine(runtimeDir, "msllive"));
        File.WriteAllBytes(Path.Combine(runtimeDir, "version.dll"), new byte[] { 0x42 });
        File.WriteAllText(Path.Combine(runtimeDir, "msllive", "MslLive.Agent.dll"), "fake agent");
        fakeSysVer = Path.Combine(gameDir, "fake_system32_version.dll");
        File.WriteAllBytes(fakeSysVer, new byte[] { 0x99 });
        origSysVer = DevModeInstaller.SysVersionDll;
        DevModeInstaller.SysVersionDll = fakeSysVer;
    }

    public void Dispose()
    {
        DevModeInstaller.SysVersionDll = origSysVer;
        try { Directory.Delete(gameDir, true); } catch { }
        try { Directory.Delete(runtimeDir, true); } catch { }
    }

    [Fact]
    public void Install_ThenUninstall_RoundTrip()
    {
        Assert.False(DevModeInstaller.IsInstalled(gameDir));
        DevModeInstaller.Install(gameDir, runtimeDir);

        Assert.True(DevModeInstaller.IsInstalled(gameDir));
        Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(new byte[] { 0x99 }, File.ReadAllBytes(Path.Combine(gameDir, "version_orig.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir, "msllive", "MslLive.Agent.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir, "mods", "_live", "res", "_blank.png")));
        string marker = File.ReadAllText(Path.Combine(gameDir, DevModeInstaller.Marker));
        Assert.Contains(" ", marker);   // "<版本> <ISO 时间>"

        DevModeInstaller.Uninstall(gameDir, () => false);
        Assert.False(DevModeInstaller.IsInstalled(gameDir));
        Assert.False(File.Exists(Path.Combine(gameDir, "version.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "version_orig.dll")));
        Assert.False(Directory.Exists(Path.Combine(gameDir, "msllive")));
    }

    [Fact]
    public void Uninstall_WithoutMarker_TouchesNothing()
    {
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), new byte[] { 0x77 });   // 别人的文件
        DevModeInstaller.Uninstall(gameDir, () => false);
        Assert.Equal(new byte[] { 0x77 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
    }

    [Fact]
    public void Uninstall_WhileGameRunning_LeavesEverything()
    {
        DevModeInstaller.Install(gameDir, runtimeDir);
        DevModeInstaller.Uninstall(gameDir, () => true);
        Assert.True(DevModeInstaller.IsInstalled(gameDir));   // marker 与文件全留
        Assert.True(File.Exists(Path.Combine(gameDir, "version.dll")));
    }

    [Fact]
    public void Install_WithoutRuntime_Throws()
    {
        Directory.Delete(runtimeDir, true);
        Assert.Throws<FileNotFoundException>(() => DevModeInstaller.Install(gameDir, runtimeDir));
    }
}
