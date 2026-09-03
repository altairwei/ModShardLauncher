using System;
using System.IO;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>DevModeInstaller 装/卸往返（全临时目录；System32 version.dll、「游戏运行中」
/// 判据、RuntimeDir 走注入缝）。钉：marker 存在才卸、运行中不卸、msllive-runtime 缺失即抛；
/// fix #28：EnsureInstalled 自愈门控（Dev 开 + dataPath 已知 + 未装 + 游戏关 → 装上）。
/// 动 Main.Settings/DataLoader 静态 → 挂 vanilla 串行组。</summary>
[Collection("vanilla")]
public class DevModeInstallerTests : IDisposable
{
    readonly string gameDir = Path.Combine(Path.GetTempPath(), "msl_game_" + Guid.NewGuid().ToString("N"));
    readonly string runtimeDir = Path.Combine(Path.GetTempPath(), "msl_rt_" + Guid.NewGuid().ToString("N"));
    readonly string fakeSysVer;
    readonly string origSysVer;
    readonly bool origDev;
    readonly string origDataPath;
    readonly Func<bool> origGameRunning;
    readonly Func<string>? origRuntimeDir;

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
        origDev = Main.Settings.DevMode;
        origDataPath = DataLoader.dataPath;
        origGameRunning = DevModeInstaller.GameRunning;
        origRuntimeDir = DevModeInstaller.RuntimeDirOverride;
    }

    public void Dispose()
    {
        DevModeInstaller.SysVersionDll = origSysVer;
        Main.Settings.DevMode = origDev;
        DataLoader.dataPath = origDataPath;
        DevModeInstaller.GameRunning = origGameRunning;
        DevModeInstaller.RuntimeDirOverride = origRuntimeDir;
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

    // ---- fix #28：EnsureInstalled（生产自愈调用点 = Main.Refresh → DevMode.EnsureInstalled）----

    /// <summary>门控齐备（Dev 开 + dataPath 已知 + 未装 + 游戏关）→ 装上——Task 15 只挂了
    /// 退出卸载（Window_Closing → Uninstall），装上这一半在生产里不存在：MSL 每次退出
    /// （游戏恰好关着）组件被移除且永不回来，热会话从此静默死。</summary>
    [Fact]
    public void EnsureInstalled_DevOn_FileLoaded_GameClosed_Installs()
    {
        Main.Settings.DevMode = true;
        DataLoader.dataPath = Path.Combine(gameDir, "data.win");
        DevModeInstaller.GameRunning = () => false;
        DevModeInstaller.RuntimeDirOverride = () => runtimeDir;

        Assert.False(DevModeInstaller.IsInstalled(gameDir));
        DevMode.EnsureInstalled();

        Assert.True(DevModeInstaller.IsInstalled(gameDir));
        Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir, "msllive", "MslLive.Agent.dll")));
    }

    /// <summary>已装 → no-op（幂等）：不因刷新重装——漂改 version.dll 证伪。</summary>
    [Fact]
    public void EnsureInstalled_AlreadyInstalled_NoReinstall()
    {
        Main.Settings.DevMode = true;
        DataLoader.dataPath = Path.Combine(gameDir, "data.win");
        DevModeInstaller.GameRunning = () => false;
        DevModeInstaller.RuntimeDirOverride = () => runtimeDir;
        DevModeInstaller.Install(gameDir, runtimeDir);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), new byte[] { 0x77 });   // 漂改证伪

        DevMode.EnsureInstalled();

        Assert.Equal(new byte[] { 0x77 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
    }

    /// <summary>游戏运行中 → 跳过（与 Uninstall 同一纪律：运行中不动游戏目录），
    /// 关游戏后下次刷新自动补。</summary>
    [Fact]
    public void EnsureInstalled_GameRunning_Skips()
    {
        Main.Settings.DevMode = true;
        DataLoader.dataPath = Path.Combine(gameDir, "data.win");
        DevModeInstaller.GameRunning = () => true;
        DevModeInstaller.RuntimeDirOverride = () => runtimeDir;

        DevMode.EnsureInstalled();

        Assert.False(DevModeInstaller.IsInstalled(gameDir));
    }

    /// <summary>msllive-runtime 缺失（没跑 Build-MslLive.ps1）→ Install 抛
    /// FileNotFoundException 被吞 + 只留日志——刷新/启动路径不得被文件 IO 打崩。</summary>
    [Fact]
    public void EnsureInstalled_MissingRuntime_SwallowsNoThrow()
    {
        Main.Settings.DevMode = true;
        DataLoader.dataPath = Path.Combine(gameDir, "data.win");
        DevModeInstaller.GameRunning = () => false;
        DevModeInstaller.RuntimeDirOverride = () => Path.Combine(runtimeDir, "missing");

        var ex = Record.Exception(() => DevMode.EnsureInstalled());

        Assert.Null(ex);
        Assert.False(DevModeInstaller.IsInstalled(gameDir));
    }

    /// <summary>Dev 关 → 绝不装（门控第一道）。</summary>
    [Fact]
    public void EnsureInstalled_DevOff_NoOp()
    {
        Main.Settings.DevMode = false;
        DataLoader.dataPath = Path.Combine(gameDir, "data.win");
        DevModeInstaller.GameRunning = () => false;
        DevModeInstaller.RuntimeDirOverride = () => runtimeDir;

        DevMode.EnsureInstalled();

        Assert.False(DevModeInstaller.IsInstalled(gameDir));
    }

    /// <summary>dataPath 空（MSL 刚启动还没打开 data.win 的状态）→ no-op——
    /// gameDir 未知时不得凭空动作。</summary>
    [Fact]
    public void EnsureInstalled_NoDataPath_NoOp()
    {
        Main.Settings.DevMode = true;
        DataLoader.dataPath = "";
        DevModeInstaller.GameRunning = () => false;
        DevModeInstaller.RuntimeDirOverride = () => runtimeDir;

        DevMode.EnsureInstalled();

        Assert.False(DevModeInstaller.IsInstalled(gameDir));
    }
}
