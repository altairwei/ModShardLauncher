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
            var q = Wire.Receive(server);
            Assert.Equal("queryBlanks", q.Type);
            Wire.Send(server, "blanks", new BlanksMsg { SpriteFirst = 100, PathFirst = 8 });   // Count=0：agent 不知配额，MSL 侧用 quotas 覆盖
            var v = Wire.Receive(server);
            Assert.Equal("vars", v.Type);
            var p = Wire.Receive(server);
            Assert.Equal("proof", p.Type);
            Assert.NotEmpty(Wire.Decode<ProofMsg>(p.Data).Ops);
            Wire.Send(server, "proofAck", new ProofAck { Ok = true, Verified = 64 });
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
            Wire.Receive(server);   // queryBlanks
            Wire.Send(server, "blanks", new BlanksMsg { SpriteFirst = 100, PathFirst = 8 });
            Wire.Receive(server);   // vars
            Wire.Receive(server);   // proof
            Wire.Send(server, "proofAck", new ProofAck { Ok = false, Error = "byte mismatch @entry_x", Verified = 61, Failed = 3 });
        });
        var s = NewSession(pipe);
        Assert.False(s.TryConnect());
        Assert.Contains("编码自证失败", s.LastError);
        agent.Wait();
    }

    public void Dispose() => BaselineStore.Reset();
}
