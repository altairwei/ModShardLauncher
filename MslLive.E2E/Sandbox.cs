using System;
using System.IO;
using ModShardLauncher.HotReload;
using UndertaleModLib;

namespace MslLive.E2E;

/// <summary>每测试一次性沙箱：runner_stoneshard.exe + options.ini + 沙箱 data.win（含 stub
/// 与探针）+ dev 组件（version.dll 代理 + msllive\agent + _blank.png）。目录在
/// E2EFixture\sandbox\&lt;testName&gt;\（gitignored）；创建时整目录重建，失败现场保留供事后取证
/// （agent.log / 观测文件都在里面）——不追求每次清场，只保证同测试重跑覆盖。</summary>
public sealed class Sandbox
{
    public string Dir { get; }
    public string RunnerExe => Path.Combine(Dir, "runner_stoneshard.exe");
    public string DataWin => Path.Combine(Dir, "data.win");
    public string AgentLog => Path.Combine(Dir, "msllive", "agent.log");

    /// <summary>GML 相对路径的落盘区（GEN8 名 = msl_e2e）：%LOCALAPPDATA%\msl_e2e\。
    /// 整个目录只属于 E2E 沙箱——boot 前整删，防上一轮的陈旧观测文件造成假就绪。</summary>
    public string SaveArea { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        TestDataBuilder.SandboxGameName);

    Sandbox(string dir) => Dir = dir;

    public static Sandbox Create(string testName)
    {
        string root = Path.Combine(E2EFixture.RepoRoot, "E2EFixture", "sandbox", testName);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        File.Copy(E2EFixture.RunnerExe, Path.Combine(root, "runner_stoneshard.exe"));
        if (File.Exists(E2EFixture.SeedOptionsIni))
            File.Copy(E2EFixture.SeedOptionsIni, Path.Combine(root, "options.ini"));

        var data = TestDataBuilder.Build(E2EFixture.SeedWin);
        string dataWin = Path.Combine(root, "data.win");
        using (var s = File.Create(dataWin))
            UndertaleIO.Write(s, data);

        // dev 组件生产装配器原样（version.dll/version_orig.dll/msllive\*/mods\_live\res\_blank.png）
        DevModeInstaller.Install(root, E2EFixture.RuntimeDir!);

        // 索引构建按 seed 规模定参（StoneShard 定标在小宿主上全不适用，E2E smoke 实证）：
        // min-nodes：seed 本体仅 1 code entry + 注入的 stub/探针/观察者 ≈ 十几个节点，
        //   门槛 30000 永不达标 → WaitReady 8s 兜等耗尽、hello 永报 building；门槛 8 =
        //   烟雾下限（全扫穷尽 + 迟燃时绑定必已完成——帧已在跑，规模门槛只剩非空语义）。
        // scan=full：门控探针的鸽笼保证只覆盖 ≥64KB 节点 run——seed 全部节点 ~15KB 落在
        //   头窗外且区段 < 64KB 无网格点（实证 regions 0/113，#26 语义零轮永不升级全扫）；
        //   小进程全扫毫秒级且无工作集逐出问题。cfg 是安装级调参面：真机安装不写此文件，
        //   生产默认（门控 + 平台期兜底）不变。
        // dump-nodes=1：索引名全量落 msllive\nodes.txt——proof「node not found」取证
        //   （索引名 vs 数据条目名对照的第一手证据）。
        File.WriteAllText(Path.Combine(root, "msllive", "agent.cfg"),
            "min-nodes=8\nscan=full\ndump-nodes=1");

        var sb = new Sandbox(root);
        sb.CleanSaveArea();
        return sb;
    }

    void CleanSaveArea()
    {
        try { if (Directory.Exists(SaveArea)) Directory.Delete(SaveArea, recursive: true); }
        catch { /* 上一轮 runner 残留句柄延迟释放——观测断言自会暴露 */ }
    }
}
