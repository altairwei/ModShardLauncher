using System.IO.Pipes;
using System.Security.Cryptography;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>fake client 走完到 proofAck（trampoline 门）/batch 挡板的剧本；断言 hello 字段与错误串。
/// 测试进程里自检必走 fail 路径（无常量 → NodeIndex 0 节点 / Registry 全局不可读）——
/// 这正好把 hello 的 AgentStatus 上报通道一并验证。</summary>
public class PipeProtocolTests : IDisposable
{
    static readonly string TmpDir = Path.Combine(Path.GetTempPath(), "msl_pipe_" + Guid.NewGuid().ToString("N"));
    static readonly byte[] FakeDataWin = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    public PipeProtocolTests()
    {
        Directory.CreateDirectory(TmpDir);
        File.WriteAllBytes(Path.Combine(TmpDir, "data.win"), FakeDataWin);
        AgentState.ResetForTest();
        AgentState.InitForTest(TmpDir);
        PipeServer.ResetForTest();   // 每个用例的连接都要重跑自检（否则 hello.AgentStatus 顺序相关）
        PipeServer.Start();   // 幂等；进程级后台监听
    }

    public void Dispose() { try { Directory.Delete(TmpDir, true); } catch { } }

    NamedPipeClientStream Connect()
    {
        var c = new NamedPipeClientStream(".", $"msl-live-{Environment.ProcessId}", PipeDirection.InOut);
        c.Connect(10000);
        return c;
    }

    [Fact]
    public void Handshake_ToProofGate_Shape()
    {
        using var c = Connect();

        var (t0, d0) = Wire.Receive(c);
        Assert.Equal("hello", t0);
        var hello = Wire.Decode<HelloMsg>(d0);
        Assert.Equal(Environment.ProcessId, hello.Pid);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(FakeDataWin)), hello.BootHash);
        Assert.False(hello.StubPresent);
        Assert.StartsWith("v", hello.AgentVersion);
        Assert.Contains("node index too small", hello.AgentStatus);   // 测试进程无常量 → 自检 fail 路径

        Wire.Send(c, "helloAck", new HelloAck { Accept = true });

        Wire.Send(c, "queryBlanks", new { });
        var (t1, d1) = Wire.Receive(c);
        Assert.Equal("blanks", t1);
        Assert.Equal(-1, Wire.Decode<BlanksMsg>(d1).SpriteFirst);   // 未校准

        Wire.Send(c, "vars", new VarsMsg { Ids = { ["i:x"] = 42 } });
        Wire.Send(c, "proof", new ProofMsg());
        var (t2, d2) = Wire.Receive(c);
        Assert.Equal("proofAck", t2);
        var proofAck = Wire.Decode<ProofAck>(d2);
        // Task 14 接管后：空 proof 的编码自证 trivially 过（0 op），但测试进程没有 dummy stub 节点，
        // 原生函数也没注册 → trampoline 装不上 → fail-closed 整个拒掉，绝不假装成功
        Assert.False(proofAck.Ok);
        Assert.Equal(0, proofAck.Verified);
        Assert.Contains("trampoline install failed", proofAck.Error);
        Assert.Contains("native not registered", proofAck.Error);   // 两个 stub 都装不上，LastError 为末次
        Assert.Equal(42, AgentState.VarMap!["i:x"]);               // vars 已存

        Wire.Send(c, "batch", new BatchMsg { BatchSeq = 7, Ops = { new OpMsg { Seq = 0, Entry = "e" } } });
        var (t3, d3) = Wire.Receive(c);
        Assert.Equal("receipt", t3);
        var receipt = Wire.Decode<BatchReceipt>(d3);
        Assert.Equal(7, receipt.BatchSeq);
        Assert.False(receipt.AllOk);
        Assert.Equal("apply not implemented", receipt.Ops[0].Reason);   // Task 15 挡板
    }

    [Fact]
    public void HelloAck_Reject_ClosesConnection()
    {
        using var c = Connect();
        var (t0, _) = Wire.Receive(c);
        Assert.Equal("hello", t0);
        Wire.Send(c, "helloAck", new HelloAck { Accept = false, Reason = "nope" });
        // 服务端记日志后关连接：下一次读必须 EOF（而不是挂死）
        Assert.ThrowsAny<Exception>(() => Wire.Receive(c));
    }
}
