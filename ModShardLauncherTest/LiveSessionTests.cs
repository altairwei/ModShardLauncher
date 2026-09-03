using System.Diagnostics;
using System.IO.Pipes;
using ModShardLauncher.HotReload;
using MslLive.Shared;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class LiveSessionTests : IDisposable
{
    static LiveSession NewSession(string pipe, string version = "v1") =>
        new(pipe, () => version, () => "2022.9.0.0 bc17", () => new List<(string, string)>(),
            new LiveQuotas(), () => new List<(int, string)> { (100, "") });

    static NamedPipeServerStream NewServer(string pipe)
    {
        var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task.Run(() => s.WaitForConnection());
        return s;
    }

    // mock agent 首发前必须等连接就位——否则 Write 抢在 client Connect 前抛 InvalidOperationException，
    // agent 任务静默死亡、client 侧首收无超时永挂（全量回归实测挂起形态，Task 11 复盘）。
    static void WaitConnected(NamedPipeServerStream s)
    {
        for (int i = 0; i < 2000 && !s.IsConnected; i++) Thread.Sleep(5);
        if (!s.IsConnected) throw new InvalidOperationException("mock agent: client never connected");
    }

    CompileRecord SeedBaseline()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "msl_sess_" + Guid.NewGuid().ToString("N") + ".win");
        File.Copy(TestData.VanillaPath, tmp);
        CompileRecord rec;
        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var data = UndertaleModLib.UndertaleIO.Read(fs, w => { });
            rec = BaselineStore.Register(data, tmp, new List<LiveTextureEntry>());
        }
        return rec;
    }

    [Fact]
    public void Handshake_FullFlow_Push_Receipt_RoundTrip()
    {
        var rec = SeedBaseline();
        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(pipe);
        var agent = Task.Run(() =>
        {
            WaitConnected(server);
            Wire.Send(server, "hello", new HelloMsg
                { AgentVersion = "v1", Pid = 1, BootHash = rec.Hash, StubPresent = true, AgentStatus = "ok" });
            var ack = Wire.Receive(server);
            Assert.Equal("helloAck", ack.Type);
            Assert.True(Wire.Decode<HelloAck>(ack.Data).Accept);
            var v = Wire.Receive(server);
            Assert.Equal("vars", v.Type);
            var p = Wire.Receive(server);
            Assert.Equal("proof", p.Type);
            Assert.NotEmpty(Wire.Decode<ProofMsg>(p.Data).Ops);
            Wire.Send(server, "proofAck", new ProofAck { Ok = true, Verified = 64 });
            // fix-loop #12：blanks 在 proofAck 之后获取（两跳设计——trampoline 装上才有回报）
            var q = Wire.Receive(server);
            Assert.Equal("queryBlanks", q.Type);
            Wire.Send(server, "blanks", new BlanksMsg { SpriteFirst = 100, PathFirst = 8 });   // Count=0：agent 不知配额，MSL 侧用 quotas 覆盖
            var b = Wire.Receive(server);
            var batch = Wire.Decode<BatchMsg>(b.Data);
            Wire.Send(server, "receipt", new BatchReceipt
            {
                BatchSeq = batch.BatchSeq, AllOk = true, ApplyMs = 3,
                Ops = { new OpReceipt { Seq = 0, Entry = "e", Ok = true } },
            });
        });

        var s = NewSession(pipe);
        Assert.True(s.TryConnect());
        Assert.Equal(LiveSessionState.Active, s.State);
        Assert.NotNull(s.Alloc);
        var receipt = s.PushBatch(new BatchMsg { Ops = { new OpMsg { Seq = 0, Entry = "e" } } });
        Assert.NotNull(receipt);
        Assert.True(receipt!.AllOk);
        agent.Wait();
        s.End();
    }

    [Fact]
    public void Handshake_VersionMismatch_Rejected()
    {
        var rec = SeedBaseline();
        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(pipe);
        var agent = Task.Run(() =>
        {
            WaitConnected(server);
            Wire.Send(server, "hello", new HelloMsg
                { AgentVersion = "OLD", Pid = 1, BootHash = rec.Hash, StubPresent = true, AgentStatus = "ok" });
            var ack = Wire.Receive(server);
            Assert.False(Wire.Decode<HelloAck>(ack.Data).Accept);
        });
        var s = NewSession(pipe);
        Assert.False(s.TryConnect());
        Assert.Equal(LiveSessionState.Ended, s.State);
        Assert.Contains("重装 Dev 模式组件", s.LastError);
        agent.Wait();
    }

    [Fact]
    public void Handshake_UnknownBootHash_Rejected()
    {
        SeedBaseline();
        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(pipe);
        var agent = Task.Run(() =>
        {
            WaitConnected(server);
            Wire.Send(server, "hello", new HelloMsg
                { AgentVersion = "v1", Pid = 1, BootHash = "DEADBEEF", StubPresent = true, AgentStatus = "ok" });
            var ack = Wire.Receive(server);
            Assert.False(Wire.Decode<HelloAck>(ack.Data).Accept);
        });
        var s = NewSession(pipe);
        Assert.False(s.TryConnect());
        Assert.Contains("重启游戏", s.LastError);
        agent.Wait();
    }

    [Fact]
    public void Handshake_ProofFailure_Rejected()
    {
        var rec = SeedBaseline();
        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(pipe);
        var agent = Task.Run(() =>
        {
            WaitConnected(server);
            Wire.Send(server, "hello", new HelloMsg
                { AgentVersion = "v1", Pid = 1, BootHash = rec.Hash, StubPresent = true, AgentStatus = "ok" });
            Wire.Receive(server);   // helloAck
            Wire.Receive(server);   // vars
            Wire.Receive(server);   // proof（失败后不再获取 blanks——fix-loop #12 顺序）
            Wire.Send(server, "proofAck", new ProofAck { Ok = false, Error = "byte mismatch @entry_x", Verified = 61, Failed = 3 });
        });
        var s = NewSession(pipe);
        Assert.False(s.TryConnect());
        Assert.Contains("编码自证失败", s.LastError);
        agent.Wait();
    }

    // fix-loop #12 回归（真机 23:03 首连实测）：agent 的上报是两跳设计——stub GML 的
    // msl_live_report 调用先命中编译期注入的 dummy 脚本，proof 阶段 Trampoline.Install
    // 才把 dummy 换成 call.v 原生，校准只可能发生在安装后的下一游戏帧。因此 queryBlanks
    // 在 proof 之前只能拿到 -1：若 MSL 把 AcquireBlanks 排在 proof 前（旧顺序），
    // 5×500ms 重试全空 → 死锁——blanks 永远无法校准，热会话永远建立不了。
    // 本用例的 mock 如实建模：proof 之前回 -1；proof 之后第一问仍 -1（trampoline
    // 刚装、下一帧未跑），第二问才回真实值——顺带钉住 500ms 重试语义。
    [Fact]
    public void Handshake_BlanksUncalibratedUntilProof_StillSucceeds()
    {
        var rec = SeedBaseline();
        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(pipe);
        var agent = Task.Run(() =>
        {
            try
            {
                WaitConnected(server);
                Wire.Send(server, "hello", new HelloMsg
                    { AgentVersion = "v1", Pid = 1, BootHash = rec.Hash, StubPresent = true, AgentStatus = "ok" });
                bool proofSeen = false;
                int queriesAfterProof = 0;
                while (true)
                {
                    var m = Wire.Receive(server);
                    switch (m.Type)
                    {
                        case "helloAck":
                        case "vars":
                            break;
                        case "proof":
                            proofSeen = true;
                            Wire.Send(server, "proofAck", new ProofAck { Ok = true, Verified = 64 });
                            break;
                        case "queryBlanks":
                            if (!proofSeen || ++queriesAfterProof == 1)
                                Wire.Send(server, "blanks", new BlanksMsg());   // 未校准 = 全 -1
                            else
                                Wire.Send(server, "blanks", new BlanksMsg { SpriteFirst = 100, PathFirst = 8 });
                            break;
                        default:
                            return;   // 本用例不推 batch；会话结束（pipe 关闭）也走异常退出
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        var s = NewSession(pipe);
        Assert.True(s.TryConnect(), "旧顺序死锁形态：" + s.LastError);
        Assert.Equal(LiveSessionState.Active, s.State);
        Assert.NotNull(s.Alloc);
        s.End();
        agent.Wait();
    }

    // fix-loop #25（14:54 真机「Pipe is broken」）：MSL 长驻跨游戏重启——旧游戏 13:50
    // 退出后 12:53 会话 State 仍 Active（死管道只在下次 IO 才暴露），14:54 编译复用旧
    // 会话 = 往死管道推送，白烧一次 4-5min 编译。修复：会话绑定目标进程 PID+启动时刻，
    // 复用前校验存活（BuildAndPush 布线）。
    [Fact]
    public void TargetStillRunning_TrueOnlyWhileTargetProcessAlive()
    {
        var s = NewSession("msl-test-" + Guid.NewGuid().ToString("N"));
        Assert.False(s.TargetStillRunning());          // 未绑定目标（PID 0）→ 不可复用

        using var self = Process.GetCurrentProcess();
        s.TargetPid = self.Id;
        s.TargetStart = self.StartTime;
        Assert.True(s.TargetStillRunning());           // 活进程 + 启动时刻一致 → 可复用

        s.TargetStart = self.StartTime.AddMinutes(-5);
        Assert.False(s.TargetStillRunning());          // 启动时刻不符（PID 复用形态）→ 不可复用
        s.TargetStart = self.StartTime;

        using var victim = Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping -n 30 127.0.0.1 > NUL") { CreateNoWindow = true, UseShellExecute = false })!;
        s.TargetPid = victim.Id;
        s.TargetStart = victim.StartTime;
        Assert.True(s.TargetStillRunning());           // 目标活着
        victim.Kill(entireProcessTree: true);
        Assert.True(victim.WaitForExit(5000));
        Assert.False(s.TargetStillRunning());          // 目标已退出（14:54 形态）→ 不可复用
    }

    public void Dispose() => BaselineStore.Reset();
}
