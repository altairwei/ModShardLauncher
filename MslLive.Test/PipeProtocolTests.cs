using System.IO.Pipes;
using System.Security.Cryptography;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>fake client 走完握手 → proofAck（trampoline 门）→ batch（ApplyEngine 接管）的剧本；
/// 断言 hello 字段与错误串。
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
        // Task 15 接管后：测试进程 NodeIndex 为空 → Phase 1 resolve 失败立即回执（无 Pump 必要）
        Assert.Equal("resolve", receipt.Ops[0].Stage);
        Assert.Equal("node not found", receipt.Ops[0].Reason);
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

    [Fact]
    public void Accept_PendingWhenClientConnects_LateConnectionServed()
    {
        // fix-loop #7 现场时序：客户端在服务端 accept 已经挂起之后才连（真机上这一步的
        // overlapped 完成投递丢失 → 28 分钟不取件）。测试宿主 IOCP 正常，这里钉的是
        // 行为契约：accept 挂起中收到连接必须随即完成并送出 hello。
        Thread.Sleep(300);   // 让监听线程先挂起在 accept 上
        using var c = Connect();
        var (t0, _) = Wire.Receive(c);
        Assert.Equal("hello", t0);
    }

    [Fact]
    public void IdleConnection_StaysResponsive_AcrossFlushCycles()
    {
        // 空闲要跨多个 200ms 轮询周期（出站回执转发节奏）：连接必须保持活着且继续可服务
        using var c = Connect();
        var (t0, _) = Wire.Receive(c);
        Assert.Equal("hello", t0);
        Wire.Send(c, "helloAck", new HelloAck { Accept = true });
        Thread.Sleep(600);
        Wire.Send(c, "queryBlanks", new { });
        var (t1, d1) = Wire.Receive(c);
        Assert.Equal("blanks", t1);
        Assert.Equal(-1, Wire.Decode<BlanksMsg>(d1).SpriteFirst);
    }

    [Fact]
    public void SequentialConnections_EachServedIndependently()
    {
        // accept 循环必须能连续服务多轮连接（每轮：连上 → hello → 不握手直接断开）
        for (int i = 0; i < 3; i++)
        {
            using var c = Connect();
            var (t0, _) = Wire.Receive(c);
            Assert.Equal("hello", t0);
        }
    }
}
