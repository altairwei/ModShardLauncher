using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MslLive.Shared;
using Serilog;

// 测试钩子构造器是 internal——放行测试程序集（替代方案是把测试构造器做成 public 污染 API）
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ModShardLauncherTest")]

namespace ModShardLauncher.HotReload;

public enum LiveSessionState { NoSession, Handshaking, Active, Ended }

public sealed class LiveSession : IDisposable
{
    public static LiveSession? Current { get; private set; }

    public LiveSessionState State { get; private set; } = LiveSessionState.NoSession;
    public SessionState? Alloc { get; private set; }
    public HelloMsg? Hello { get; private set; }
    public string LastError { get; private set; } = "";
    public event Action<string>? StatusChanged;

    readonly string pipeName;
    readonly Func<string> mslVersion;
    readonly Func<string> gen8Version;
    readonly Func<IReadOnlyList<(string mod, string targetVersion)>> enabledMods;
    readonly LiveQuotas quotas;
    readonly Func<IReadOnlyList<(int shellIndex, string parent)>> shellBuckets;
    NamedPipeClientStream? pipe;
    int batchSeq;

    internal LiveSession(string pipeName, Func<string> mslVersion, Func<string> gen8Version,
        Func<IReadOnlyList<(string mod, string targetVersion)>> enabledMods,
        LiveQuotas quotas, Func<IReadOnlyList<(int, string)>> shellBuckets)
    {
        this.pipeName = pipeName;
        this.mslVersion = mslVersion;
        this.gen8Version = gen8Version;
        this.enabledMods = enabledMods;
        this.quotas = quotas;
        this.shellBuckets = shellBuckets;
    }

    public static LiveSession ForRunningGame(LiveQuotas quotas,
        Func<IReadOnlyList<(int, string)>> shellBuckets)
    {
        // 多个 StoneShard 进程时取最新启动的：残留挂起进程（崩溃循环遗留）的 agent
        // 仍在监听自己的 msl-live-<pid> 管道但线程冻结，FirstOrDefault 任挑一个会让
        // 握手静默超时（Task 16 循环 #6 真机实测 13:47 的 receive timeout）
        var procs = Process.GetProcessesByName("StoneShard")
            .Select(p => { DateTime st; try { st = p.StartTime; } catch { st = DateTime.MinValue; } return (Proc: p, Start: st); })
            .OrderByDescending(t => t.Start)
            .ToList();
        if (procs.Count == 0)
            throw new InvalidOperationException("StoneShard.exe 未运行");
        var (proc, start) = procs[0];
        Log.Information("[live] 目标进程 StoneShard#{Pid}（启动于 {Start:HH:mm:ss}，共 {Count} 个候选）",
            proc.Id, start, procs.Count);
        return new LiveSession($"msl-live-{proc.Id}",
            () => Main.Instance.mslVersion,
            () => Gen8Guard.VersionOf(ModLoader.Data),
            () => Controls.ModInfos.Instance.Mods.Where(m => m.isEnabled)
                .Select(m => (m.Name, m.instance.TargetVersion)).ToList(),
            quotas, shellBuckets);
    }

    public bool TryConnect()
    {
        State = LiveSessionState.Handshaking;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(2000);
            // 首收也要限时：agent 连上后死了/不发 hello 是真实病态（UI 线程永挂），fail-closed
            var (type, data) = ReceiveWithTimeout(10000);
            if (type != "hello") return Fail($"protocol error: expected hello, got {type}");
            Hello = Wire.Decode<HelloMsg>(data);

            string? reject = Validate(Hello);
            if (reject == null)
            {
                var rec = BaselineStore.LockBaseline(Hello.BootHash, out bool isNew, out string reason);
                if (rec == null) reject = reason;
                else if (isNew) Alloc = null;   // 新游戏会话：池状态重来
            }
            Wire.Send(pipe, "helloAck", new HelloAck { Accept = reject == null, Reason = reject ?? "" });
            if (reject != null) return Fail(reject);

            var boot = BaselineStore.BootBaseline!;
            Wire.Send(pipe, "vars", new VarsMsg
            {
                Ids = VarIdSimulator.Simulate(boot.Product),
            });

            Wire.Send(pipe, "proof", ProofBuilder.Build(boot.Product, boot.StrgIndex.Value));
            var (pt, pd) = ReceiveWithTimeout(30000);
            if (pt != "proofAck") return Fail($"protocol error: expected proofAck, got {pt}");
            var proof = Wire.Decode<ProofAck>(pd);
            if (!proof.Ok)
                return Fail($"编码自证失败（{proof.Verified} 通过 / {proof.Failed} 失败）：{proof.Error}");

            // fix-loop #12：blanks 必须在 proofAck 之后获取。agent 的上报是两跳设计（Task 14）：
            // stub GML 的 msl_live_report 调用先命中编译期注入的 dummy 脚本，proof 阶段
            // Trampoline.Install 才把它换成 call.v 原生——校准只可能发生在安装后的下一游戏帧。
            // 旧顺序把 AcquireBlanks 排在 proof 前，queryBlanks 恒 -1（5×500ms 全空）→
            // 结构性死锁：热会话永远建立不了（真机 23:03 首连实测，agent 零 report calibrated）。
            if (Alloc == null && !AcquireBlanks()) return Fail(LastError);

            State = LiveSessionState.Active;
            Current = this;
            StatusChanged?.Invoke("热会话已建立");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"连接失败: {ex.Message}");
        }
    }

    bool AcquireBlanks()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Wire.Send(pipe!, "queryBlanks", new { });
            var (bt, bd) = ReceiveWithTimeout(5000);
            if (bt != "blanks") { LastError = $"protocol error: expected blanks, got {bt}"; return false; }
            var blanks = Wire.Decode<BlanksMsg>(bd);
            if (blanks.SpriteFirst >= 0)
            {
                // agent 只知道 First（上报打包值）；Count 以本地配额为准（与 Task 8 注入的分配循环上界一致）
                blanks.SpriteCount = quotas.BlankSprites;
                blanks.PathCount = quotas.BlankPaths;
                Alloc = new SessionState(quotas, shellBuckets());
                Alloc.SetBlanks(blanks);
                return true;
            }
            Thread.Sleep(500);   // trampoline 刚装上、游戏线程下一帧还没跑（Step 的 report 未校准）——等一帧再问
        }
        LastError = "blank 分配失败（_blank.png 缺失或游戏未过启动阶段）——重装 Dev 组件或稍后再试";
        return false;
    }

    (string Type, System.Text.Json.JsonElement Data) ReceiveWithTimeout(int timeoutMs)
    {
        var read = Task.Run(() => Wire.Receive(pipe!));
        if (Task.WhenAny(read, Task.Delay(timeoutMs)).Result != read)
            throw new TimeoutException($"receive timeout ({timeoutMs} ms)");
        return read.Result;
    }

    string? Validate(HelloMsg hello)
    {
        if (hello.Protocol != 1) return $"agent 协议版本不符（{hello.Protocol} vs 1）——重装 Dev 模式组件";
        if (hello.AgentVersion != mslVersion()) return "agent 与 MSL 版本不符——重装 Dev 模式组件";
        if (hello.AgentStatus != "ok") return $"agent 自检失败：{hello.AgentStatus}";
        if (!hello.StubPresent) return "当前 data.win 无热加载 stub——开 Dev 模式重新编译并重启游戏";
        foreach (var (mod, tv) in enabledMods())
            if (!string.IsNullOrEmpty(tv) && tv != gen8Version())
                return $"mod {mod} 的 TargetVersion（{tv}）与游戏版本（{gen8Version()}）不符";
        return null;
    }

    /// <summary>推送一批 ops 并等回执。任何异常 = 会话结束（断线降级纯写盘，spec §8）。
    /// 回执在 agent 应用完成后才发出（Task 15：游戏线程帧内应用）；游戏暂停/卡住时
    /// 30s 超时是诚实的失败，不是 bug。</summary>
    public BatchReceipt? PushBatch(BatchMsg batch, int timeoutMs = 30000)
    {
        if (State != LiveSessionState.Active || pipe == null) return null;
        batch.BatchSeq = ++batchSeq;
        try
        {
            Wire.Send(pipe, "batch", batch);
            var (type, data) = ReceiveWithTimeout(timeoutMs);
            if (type != "receipt") { Fail($"protocol error: expected receipt, got {type}"); return null; }
            return Wire.Decode<BatchReceipt>(data);
        }
        catch (Exception ex)
        {
            Fail($"推送失败: {ex.Message}");
            return null;
        }
    }

    bool Fail(string why)
    {
        LastError = why;
        Log.Warning("[live] {why}", why);
        End();
        return false;
    }

    public void End()
    {
        State = LiveSessionState.Ended;
        try { pipe?.Dispose(); } catch { }
        pipe = null;
        if (ReferenceEquals(Current, this)) Current = null;
        StatusChanged?.Invoke("热会话结束（纯写盘模式）");
    }

    public void Dispose() => End();
}
