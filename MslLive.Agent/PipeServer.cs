using System.IO.Pipes;
using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>\\.\pipe\msl-live-&lt;pid&gt; 单客户端服务。后台线程自持，永远不阻塞游戏线程。
/// 首连跑自检（结果进 hello）；helloAck 拒收 → 关连接继续监听（MSL 可能换版本重来）。
/// proof/batch 是 Task 14/15 的诚实挡板：明确回错，绝不假装成功。</summary>
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

        while (true)
        {
            var (type, data) = Wire.Receive(s);
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
                    // Task 14 接管前的诚实挡板
                    Wire.Send(s, "proofAck", new ProofAck { Ok = false, Error = "encoder not implemented" });
                    break;
                case "batch":
                    // Task 15 接管前的诚实挡板
                    var b = Wire.Decode<BatchMsg>(data);
                    Wire.Send(s, "receipt", new BatchReceipt
                    {
                        BatchSeq = b.BatchSeq,
                        AllOk = false,
                        Ops = b.Ops.Select(o => new OpReceipt
                        { Seq = o.Seq, Entry = o.Entry, Ok = false, Stage = "validate", Reason = "apply not implemented" }).ToList(),
                    });
                    break;
                default:
                    AgentState.Log("unknown msg: " + type);
                    break;
            }
        }
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
