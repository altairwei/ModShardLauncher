using System;
using System.IO;
using Serilog;

namespace ModShardLauncher.HotReload;

/// <summary>把 dev 组件装进游戏目录：version.dll（Task 12 bootstrap 代理）+ version_orig.dll
/// （System32 副本，代理转发目标）+ msllive\（agent publish 产物）+ _blank.png + marker。
/// 卸载只认 marker（不是我们装的绝不碰），游戏运行中拒绝卸载（文件占用之外还有稳定性）。</summary>
public static class DevModeInstaller
{
    public const string Marker = "msllive/installed-by-msl.txt";

    // 测试注入缝（真实默认 = System32 副本 / StoneShard 进程探测；测试换临时文件与谓词）
    internal static string SysVersionDll =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
    internal static Func<bool> GameRunning =
        () => System.Diagnostics.Process.GetProcessesByName("StoneShard").Length > 0;

    public static bool IsInstalled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, Marker)) && File.Exists(Path.Combine(gameDir, "version.dll"));

    public static void Install(string gameDir) => Install(gameDir, RuntimeDir());

    internal static void Install(string gameDir, string runtimeDir)
    {
        if (!Directory.Exists(runtimeDir))
            throw new FileNotFoundException($"msllive-runtime 不存在——先跑 Build-MslLive.ps1: {runtimeDir}");
        Directory.CreateDirectory(Path.Combine(gameDir, "msllive"));
        File.Copy(Path.Combine(runtimeDir, "version.dll"), Path.Combine(gameDir, "version.dll"), true);
        File.Copy(SysVersionDll, Path.Combine(gameDir, "version_orig.dll"), true);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(runtimeDir, "msllive")))
            File.Copy(f, Path.Combine(gameDir, "msllive", Path.GetFileName(f)), true);
        PngExtractor.WriteBlankPng(Path.Combine(gameDir, PngExtractor.ResRelRoot));
        File.WriteAllText(Path.Combine(gameDir, Marker),
            $"{Main.Instance?.mslVersion ?? "v?"} {DateTime.Now:O}");   // 测试进程无 WPF 主窗 → v?
        Log.Information("[live] dev components installed to {gameDir}", gameDir);
    }

    public static void Uninstall(string gameDir) => Uninstall(gameDir, GameRunning);

    internal static void Uninstall(string gameDir, Func<bool> gameRunning)
    {
        if (!File.Exists(Path.Combine(gameDir, Marker))) return;   // 不是我们装的，不碰
        if (gameRunning())
        {
            Log.Information("[live] game running, dev components left in place ({gameDir})", gameDir);
            return;
        }
        TryDelete(Path.Combine(gameDir, "version.dll"));
        TryDelete(Path.Combine(gameDir, "version_orig.dll"));
        try { Directory.Delete(Path.Combine(gameDir, "msllive"), true); } catch { }
        Log.Information("[live] dev components uninstalled from {gameDir}", gameDir);
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    static string RuntimeDir() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "msllive-runtime");
}
