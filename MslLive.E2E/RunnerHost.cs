using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MslLive.E2E;

/// <summary>runner 进程生命周期：spawn → 就绪等待（观察者首帧落盘期望值 = 房间起、oBoot 活、
/// Step 在跑、探针可调、观测通道通）→ Dispose 收尸。纪律：只杀自己 spawn 的进程，
/// 绝不碰用户的东西（runner_stoneshard.exe 是沙箱内私拷贝，与真机 StoneShard 无关）。</summary>
public sealed class RunnerHost : IDisposable
{
    readonly string sandboxDir;
    readonly string saveArea;
    Process? proc;

    public Process Proc => proc ?? throw new InvalidOperationException("runner 未启动");

    public RunnerHost(string sandboxDir, string saveArea)
    {
        this.sandboxDir = sandboxDir;
        this.saveArea = saveArea;
    }

    public void Start()
    {
        proc = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(sandboxDir, "runner_stoneshard.exe"),
            WorkingDirectory = sandboxDir,
            UseShellExecute = false,
        });
    }

    /// <summary>等观察者首帧落盘期望值——比 manager 探针更强的就绪判据（整条观测链已通）。
    /// seed 秒级 boot，60s 上限是给 agent 注入 + 首次索引构建留裕量。</summary>
    public void WaitReady(string expectedValue, int timeoutMs = 60000)
    {
        string resultFile = Path.Combine(saveArea, TestDataBuilder.ResultFile);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (proc == null) throw new InvalidOperationException("runner 未启动");
            if (proc.HasExited)
                throw new InvalidOperationException(
                    $"runner 启动后退出（码 {proc.ExitCode}）——见 {Path.Combine(sandboxDir, "msllive", "agent.log")}");
            try
            {
                if (File.ReadAllText(resultFile).Trim() == expectedValue) return;
            }
            catch { /* 文件未出现/写入中——继续等 */ }
            Thread.Sleep(250);
        }
        throw new TimeoutException(
            $"runner {timeoutMs}ms 未就绪（观测文件={(File.Exists(resultFile) ? File.ReadAllText(resultFile) : "<无>")}）"
            + $"——见 {Path.Combine(sandboxDir, "msllive", "agent.log")}");
    }

    public void Dispose()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* 已退出的竞态无妨 */ }
        proc?.Dispose();
        proc = null;
    }
}
