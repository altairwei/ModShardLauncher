using System.IO.Pipes;
using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>\\.\pipe\msl-live-&lt;pid&gt; 单客户端服务。后台线程自持，永远不阻塞游戏线程。
/// 首连跑自检（结果进 hello）；helloAck 拒收 → 关连接继续监听（MSL 可能换版本重来）。
/// proof 走 ProofVerify + Trampoline（Task 14）；batch 走 ApplyEngine 两阶段（Task 15）：
/// Phase 1 失败立即回执；成功则等游戏线程 Pump 完置出站信箱，本循环在接收超时间隙轮询转发
/// （游戏线程写 pipe 可能阻塞 VM——MSL 30s 超时兜底，游戏暂停 = 诚实超时）。</summary>
public static class PipeServer
{
    static bool started;
    static bool selfChecked;

    /// <summary>测试复位：下次连接重跑自检（selfChecked 是进程级一次性，测试需要每用例重跑）。</summary>
    internal static void ResetForTest() => selfChecked = false;

    public static void Start()
    {
        if (started) return;
        started = true;
        new Thread(AcceptLoop) { IsBackground = true, Name = "msl-live-pipe" }.Start();
    }

    static void AcceptLoop()
    {
        string name = $"msl-live-{Environment.ProcessId}";
        while (true)
        {
            NamedPipeServerStream? s = null;
            try
            {
                s = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                s.WaitForConnection();
                Handle(s);
            }
            catch (Exception ex) { AgentState.Log("pipe conn: " + ex.Message); }
            finally { s?.Dispose(); }
        }
    }

    static void Handle(NamedPipeServerStream s)
    {
        if (!selfChecked) { SelfCheck(); selfChecked = true; }

        Wire.Send(s, "hello", new HelloMsg
        {
            AgentVersion = AgentState.AgentVersion,
            Pid = Environment.ProcessId,
            BootHash = AgentState.BootHash,
            StubPresent = AgentState.StubPresent,
            AgentStatus = AgentState.Status,
        });

        var (t0, d0) = Wire.Receive(s);
        if (t0 != "helloAck") { AgentState.Log($"expected helloAck, got {t0}"); return; }
        var ack = Wire.Decode<HelloAck>(d0);
        if (!ack.Accept) { AgentState.Log("MSL rejected: " + ack.Reason); return; }

        var rx = new Wire.ReceiveState();
        while (true)
        {
            var msg = Wire.TryReceive(s, rx, 200);
            if (msg == null) { FlushReceipt(s); continue; }   // 空闲间隙：转发 Pump 完成的回执
            var (type, data) = msg.Value;
            switch (type)
            {
                case "queryBlanks":
                    Wire.Send(s, "blanks", AgentState.Blanks.Ready
                        ? new BlanksMsg { SpriteFirst = AgentState.Blanks.SpriteFirst, PathFirst = AgentState.Blanks.PathFirst }
                        : new BlanksMsg());   // 未校准 = 全 -1，MSL 侧会重试或拒收
                    break;
                case "vars":
                    AgentState.VarMap = Wire.Decode<VarsMsg>(data).Ids;
                    AgentState.Log($"vars received: {AgentState.VarMap.Count} entries");
                    break;
                case "proof":
                {
                    var pm = Wire.Decode<ProofMsg>(data);
                    var pAck = ProofVerify.Run(pm);
                    // 编码自证过了才装 trampoline；装不上 = 自证不可信（编译器形态/注册表假设破裂），
                    // 整个 proofAck 拒掉（fail-closed，MSL 侧拒绝开会话）
                    if (pAck.Ok && !Trampoline.Install())
                        pAck = new ProofAck
                        {
                            Ok = false, Verified = pAck.Verified, Failed = pAck.Failed,
                            Error = "trampoline install failed: " + Trampoline.LastError,
                        };
                    Wire.Send(s, "proofAck", pAck);
                    break;
                }
                case "batch":
                {
                    var b = Wire.Decode<BatchMsg>(data);
                    if (b.Ops.Count == 0)   // 空批 = vacuous 成功，立即回执（否则无 op 可泵、回执永远不来）
                    {
                        Wire.Send(s, "receipt", new BatchReceipt { BatchSeq = b.BatchSeq, AllOk = true });
                        break;
                    }
                    var failed = ApplyEngine.Enqueue(b);
                    if (failed != null)   // Phase 1 整批弃（spec D4）→ 立即回执，无 Pump 必要
                        Wire.Send(s, "receipt", new BatchReceipt
                        { BatchSeq = b.BatchSeq, AllOk = false, Ops = failed });
                    // 成功入队：回执待游戏线程 Pump 完成后进出站信箱，由本循环超时轮询转发
                    break;
                }
                default:
                    AgentState.Log("unknown msg: " + type);
                    break;
            }
            FlushReceipt(s);
        }
    }

    static void FlushReceipt(NamedPipeServerStream s)
    {
        var r = ApplyEngine.TryTakeReceipt();
        if (r != null) Wire.Send(s, "receipt", r);
    }

    static void SelfCheck()
    {
        int nodes = NodeIndex.Build();
        if (nodes < 30000) AgentState.Fail($"node index too small ({nodes})");
        if (!Registry.Bootstrap()) AgentState.Fail("registry bootstrap failed");
        AgentState.StubPresent = NodeIndex.TryGet("gml_Object_o_msl_live_Step_0", out _);
        AgentState.Log($"self-check: nodes={nodes} registry={Registry.Count} stub={AgentState.StubPresent} status={AgentState.Status}");
    }
}
