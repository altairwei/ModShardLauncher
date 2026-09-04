using System.IO;
using Xunit;

namespace MslLive.E2E;

/// <summary>E2E fixture 定位与校验。两条腿缺一不可：
/// 1) runner + seed（gm_stub_2022_9.7z 解包产物，E2EFixture\gm_stub_2022_9\）
/// 2) msllive-runtime（Build-MslLive.ps1 装配：version.dll 代理 + msllive\agent）
/// 任一缺失 → 测试 Skip（带指引）而非红——fixture 是本机资产，不是代码正确性判据。</summary>
public static class E2EFixture
{
    /// <summary>仓库根 = 从测试程序集向上找 ModShardLauncher.sln 的目录。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureDir => Path.Combine(RepoRoot, "E2EFixture", "gm_stub_2022_9");
    public static string RunnerExe => Path.Combine(FixtureDir, "runner_stoneshard.exe");
    public static string SeedWin => Path.Combine(FixtureDir, "seed.data.win");
    public static string SeedOptionsIni => Path.Combine(FixtureDir, "seed.options.ini");

    /// <summary>msllive-runtime 目录（无则 null）——Build-MslLive.ps1 装配到主工程 bin 下。</summary>
    public static string? RuntimeDir => FindRuntimeDir();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ModShardLauncher.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    static string? FindRuntimeDir()
    {
        // Debug 在前：E2E 通常在开发循环里跑（agent/主工程刚 build 过的最新形态）
        string[] candidates =
        {
            Path.Combine(RepoRoot, "bin", "Debug", "net6.0-windows", "msllive-runtime"),
            Path.Combine(RepoRoot, "bin", "Release", "net6.0-windows", "msllive-runtime"),
        };
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "version.dll")) &&
                File.Exists(Path.Combine(c, "msllive", "MslLive.Agent.dll")))
                return c;
        return null;
    }
}

/// <summary>fixture 缺失即 Skip 的 Fact。E2E 会 spawn 真 runner 进程（秒级 boot、桌面开窗
/// 20-60s）、走真管道、真 VM 应用——慢测试不掺进单测节奏；fixture 在就跑（这正是流水线本线）。</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!File.Exists(E2EFixture.RunnerExe) || !File.Exists(E2EFixture.SeedWin))
        {
            Skip = $"E2E fixture 缺失（{E2EFixture.FixtureDir} 需要 runner_stoneshard.exe + seed.data.win，"
                 + "来自 gm_stub_2022_9.7z 解包，gitignored）";
            return;
        }
        if (E2EFixture.RuntimeDir == null)
            Skip = "msllive-runtime 缺失——先跑 Build-MslLive.ps1（装配 version.dll 代理 + agent）";
    }
}
