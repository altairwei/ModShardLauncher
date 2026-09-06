using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Models;
using Xunit;

namespace MslLive.E2E;

/// <summary>E2E 驱动核心：在无 WPF 宿主的测试进程里装配 MSL 客户端侧全部静态状态，把
/// BuildAndPush 的会话寻径（ForRunningGame → ForRunningGameOverride 测试缝）钉到沙箱
/// runner 的 PID 上。每测试独占全套（新沙箱 + 新基线 + 新 runner + 新会话）——静态是进程级
/// 的，Boot() 先清上一个测试的残留。生产代码零改动：BuildAndPush 入口原样调。</summary>
public sealed class LiveHarness : IDisposable
{
    Sandbox? sandbox;
    RunnerHost? runner;

    public Sandbox Sandbox => sandbox ?? throw new InvalidOperationException("未 Boot");

    /// <summary>装配客户端态 + 沙箱 + boot 基线 + runner。返回前整条观测链已就绪
    /// （观察者首帧落盘 111）。</summary>
    public void Boot(string testName)
    {
        // MSL 客户端态全在 Main.Settings（普通静态 POCO，无需 WPF 主窗）。
        // 配额显式钉 LiveQuotas 默认值——必须与沙箱注入用的配额一致（blank 计数两侧共用）
        Main.Settings.DevMode = true;
        Main.Settings.LiveScriptSlots = 64;
        Main.Settings.LiveShellObjects = 8;
        Main.Settings.LiveEmptyRooms = 4;
        Main.Settings.LiveBlankSprites = 64;
        Main.Settings.LiveBlankPaths = 16;
        Main.Settings.LiveShellParents = ",,,,,,,,";   // 与沙箱注入配额一致（全空父桶，seed 无 vanilla 父类对象）
        TextureLoader.LiveScan = new();

        // 上一个测试的会话/基线/寻径缝清干净
        LiveSession.Current?.Dispose();
        LiveSession.ForRunningGameOverride = null;
        BaselineStore.Reset();

        sandbox = Sandbox.Create(testName);

        // boot 基线先注册：TryConnect 握手中的 LockBaseline 靠它命中窗口
        // （Load 两份：boot 这份被 Register 原地 SlimDown，product 另读）
        DataLoader.dataPath = sandbox.DataWin;
        DataLoader.savedDataPath = sandbox.DataWin;
        UndertaleData boot;
        using (var s = new FileStream(sandbox.DataWin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            boot = UndertaleIO.Read(s, w => { });
        BaselineStore.Register(boot, sandbox.DataWin, new List<LiveTextureEntry>());

        runner = new RunnerHost(sandbox.Dir, sandbox.SaveArea);
        runner.Start();
        runner.WaitReady(TestDataBuilder.BootValue);

        // 会话寻径测试缝：管道钉 runner PID；版本串与 agent 自报同源（"v"+agent dll FileVersion）
        string agentDll = Path.Combine(E2EFixture.RuntimeDir!, "msllive", "MslLive.Agent.dll");
        string agentVer = "v" + FileVersionInfo.GetVersionInfo(agentDll).FileVersion;
        int pid = runner.Proc.Id;
        var start = runner.Proc.StartTime;
        LiveSession.ForRunningGameOverride = (quotas, shellBuckets) =>
        {
            var s = new LiveSession($"msl-live-{pid}", () => agentVer,
                () => Gen8Guard.VersionOf(DataLoader.data!),
                () => new List<(string mod, string targetVersion)>(), quotas, shellBuckets);
            s.TargetPid = pid;       // fix-loop #25 同款绑定：复用前校验存活
            s.TargetStart = start;
            return s;
        };
    }

    /// <summary>热推一轮（生产入口原样）：另读 product 副本 → 换探针函数体 → 落盘 → BuildAndPush。
    /// 另读是正确性要求：boot 基线持有独立图，若原地改 boot 图 diff 会看不见变化。</summary>
    public HotPushResult PushProbeBody(string newBody)
    {
        return PushProduct(product =>
        {
            var code = product.Code.First(c => c.Name.Content == TestDataBuilder.ProbeScript);
            code.ReplaceGML(newBody, product);
        });
    }

    /// <summary>泛化推送入口：另读 product → 执行任意编辑（回调内可 ReplaceGML/AddFunction/
    /// 编辑对象事件/编辑 RoomCC/删条目）→ 落盘 → BuildAndPush。所有矩阵格的公共底座。</summary>
    public HotPushResult PushProduct(Action<UndertaleData> mutate)
    {
        var product = LoadFresh(Sandbox.DataWin);
        DataLoader.data = product;   // ReplaceGML/Msl 原语绑定 ModLoader.Data
        mutate(product);
        using (var s = File.Create(Sandbox.DataWin))
            UndertaleIO.Write(s, product);
        return HotPipeline.BuildAndPush(product, Sandbox.DataWin);
    }

    /// <summary>编辑对象事件（M2/M3）：按事件类型+子类型定位 Code 条目并 ReplaceGML。</summary>
    public HotPushResult EditObjectEvent(string objName, EventType evType, uint evSubtype, string newBody)
    {
        return PushProduct(product =>
        {
            string entryName = $"gml_Object_{objName}_{evType}_{evSubtype}";
            var code = product.Code.FirstOrDefault(c => c.Name.Content == entryName)
                ?? throw new InvalidOperationException($"seed 缺 {entryName}——TestDataBuilder 未建该事件");
            code.ReplaceGML(newBody, product);
        });
    }

    /// <summary>编辑 RoomCC（M4）：START 房的 CreationCodeId 条目换体。</summary>
    public HotPushResult EditRoomCC(string roomName, string newBody)
    {
        return PushProduct(product =>
        {
            var room = product.Rooms.First(r => r.Name.Content == roomName);
            var cc = room.CreationCodeId
                ?? throw new InvalidOperationException($"seed 缺 {roomName} 的 CreationCodeId——TestDataBuilder 未建");
            cc.ReplaceGML(newBody, product);
        });
    }

    /// <summary>删除脚本条目（M11）：从 product 移除 Code/Scripts/Functions 三处引用。</summary>
    public HotPushResult DeleteScript(string scriptName)
    {
        return PushProduct(product =>
        {
            // Code 层（根 + 子）
            var codes = product.Code.Where(c => c.Name.Content.Contains(scriptName)).ToList();
            foreach (var c in codes) product.Code.Remove(c);
            // Scripts 层
            var scripts = product.Scripts.Where(s => s.Name.Content.Contains(scriptName)).ToList();
            foreach (var s in scripts) product.Scripts.Remove(s);
            // Functions 层
            var fns = product.Functions.Where(f => f.Name.Content.Contains(scriptName)).ToList();
            foreach (var f in fns) product.Functions.Remove(f);
        });
    }

    /// <summary>E2E-G（mod 升级形态）：product 加 product-only 新脚本（AddFunction——
    /// 依赖者先注册，与 ZDT 的 DevTools.cs 同款顺序约束）+ 探针改体调用它——一批同时
    /// 含槽 op（新脚本热加）与 swap op（探针），CodeDiffer/BuildBatch 全生产路径。</summary>
    public HotPushResult PushProbeWithNewScript(string newScriptName, string newScriptBody, string probeBody)
    {
        return PushProduct(product =>
        {
            Msl.AddFunction(newScriptBody, newScriptName);   // 先注册：探针体的裸名调用此刻解析
            var code = product.Code.First(c => c.Name.Content == TestDataBuilder.ProbeScript);
            code.ReplaceGML(probeBody, product);
        });
    }

    /// <summary>拒批后健康断言：确认拒批不毒化管道——再推一个平凡编辑仍成功。
    /// 边界格的必备验证（拒批是合法出口，但拒批后管道必须还能用）。</summary>
    public void AssertPipelineHealthyAfterRejection()
    {
        var r = PushProbeBody("function " + TestDataBuilder.ProbeScript + "() { return 999; }");
        Assert.True(r.Attempted, "拒批后管道未启动：" + string.Join("；", r.Failures));
        Assert.True(r.Succeeded, "拒批后管道已毒化（平凡编辑失败）：" + string.Join("；", r.Failures));
        Assert.True(WaitResult("999") == "999", "拒批后观测通道已死：" + Diagnostics());
    }

    /// <summary>global 中继观测（M2/M3/M4）：等 global.e2e_target / global.e2e_roomcc 等
    /// 被对象事件/RoomCC 写入后出现在观测文件。观测通道 = oBoot Step 每帧调聚合探针，
    /// 聚合探针体里拼 global 值——M2/M3/M4 的断言目标。</summary>
    public string? WaitGlobalResult(string globalName, string expected, int timeoutMs = 20000)
    {
        // global 中继的观测值嵌在聚合探针返回串里（"probe_g|probe_a|probe_long|global_name=value"）
        // 简化：直接等观测文件包含期望值（聚合探针体在推送时改写，把 global 值拼进去）
        return WaitResult(expected, timeoutMs);
    }

    /// <summary>等观察者落盘期望值（热换体后下一帧生效）。超时返回当前值/null 供断言诊断。</summary>
    public string? WaitResult(string expected, int timeoutMs = 20000)
    {
        string file = Path.Combine(Sandbox.SaveArea, TestDataBuilder.ResultFile);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (File.ReadAllText(file).Trim() == expected) return expected; }
            catch { /* 未出现/写入中 */ }
            if (runner != null && runner.Proc.HasExited)
                throw new InvalidOperationException("runner 在等待热更生效期间退出——见 agent.log");
            Thread.Sleep(200);
        }
        try { return File.ReadAllText(file).Trim(); } catch { return null; }
    }

    /// <summary>失败诊断快照：观测文件现值 + agent.log 尾部（Assert 消息里带上，省得翻沙箱）。</summary>
    public string Diagnostics()
    {
        string result = "<无>";
        try { result = File.ReadAllText(Path.Combine(Sandbox.SaveArea, TestDataBuilder.ResultFile)).Trim(); }
        catch { }
        string logTail = "<无>";
        try
        {
            // agent 的 StreamWriter 持写句柄直到进程退出——并发读必须允许共享写，
            // 否则活 runner 期间读取必然 IOException（表现为 <无>，恰是最需要日志的时刻）
            using var fs = new FileStream(Sandbox.AgentLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) lines.Add(l);
            logTail = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 40)));
        }
        catch { }
        return $"观测文件={result}\nagent.log 尾 40 行:\n{logTail}";
    }

    static UndertaleData LoadFresh(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return UndertaleIO.Read(s, w => { });
    }

    public void Dispose()
    {
        LiveSession.ForRunningGameOverride = null;
        LiveSession.Current?.Dispose();
        runner?.Dispose();
        Main.Settings.DevMode = false;
        // 沙箱目录保留（同测试重跑覆盖；失败现场供取证）——进程必须收，目录可以留
    }
}
