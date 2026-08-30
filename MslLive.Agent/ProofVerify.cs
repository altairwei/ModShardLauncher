using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>AOB 编码自证（握手 proof 信封的处理器）：对每个 proof op——
/// ① 节点链取活 buffer（执行记录 +0x18/+0x08）；② VarCalibrator 读回真实变量 id 并做形态校验；
/// ③ Translator+BcEncoder 独立编码；④ 命中点全等直读（整 buffer 逐字节）。
/// 计划原文「全 pattern ScanAob 全局唯一命中」的两次修正：
/// ①（实现期）改为全等直读——强度 ⊇ AOB-at-hit（整 buffer ≠ 96B 窗口）；
/// ②（fix-loop #13 真机实测）删掉头窗全局唯一性残留——ScanAob 枚举的是 agent 所在
/// 进程的全地址空间，而本方法的 live/encoded 临时副本就在进程内 GC 堆上，命中数
/// 结构性 ≥3（BufPtr+live+encoded，外加 GC 未回收的增容遗留），该检查逻辑上永不可能
/// 通过（真机 35/35 全死于此；walk 后 GC 清场，外部复扫全进程仅剩 BufPtr 一 hit）；
/// 且每 op 全内存扫描实测 6m13s/35op，是 proofAck 30s 超时的直接成本。spec 无唯一性
/// 要求；运行时无消费者（apply/trampoline 均 record+0x18 直达，不走 AOB）。</summary>
public static class ProofVerify
{
    public static ProofAck Run(ProofMsg msg)
    {
        int verified = 0, failed = 0;
        var errors = new List<string>();
        foreach (var op in msg.Ops)
        {
            try
            {
                if (!NodeIndex.TryGet(op.Entry, out var node)) { failed++; errors.Add($"{op.Entry}: node not found"); continue; }
                if (node.StartOff != 0) { failed++; errors.Add($"{op.Entry}: alias child entry (startOff={node.StartOff}), not swappable"); continue; }
                byte[] live = Mem.ReadBytes(node.BufPtr, (int)node.BufLen);
                if (live.Length == 0) { failed++; errors.Add($"{op.Entry}: empty/unreadable live buffer"); continue; }

                var harvestErrors = new List<string>();
                if (!VarCalibrator.Harvest(op, live, harvestErrors))
                {
                    failed++;
                    errors.AddRange(harvestErrors.Take(3));
                    continue;
                }

                byte[] encoded = BcEncoder.Encode(op.Instructions, new Translator(op));
                if (encoded.Length != live.Length)
                {
                    failed++;
                    errors.Add($"{op.Entry}: encoded {encoded.Length}B != live {live.Length}B");
                    continue;
                }
                if (!encoded.SequenceEqual(live))
                {
                    failed++;
                    errors.Add($"{op.Entry}: encoded != live @0x{node.BufPtr:X} (first diff {FirstDiff(encoded, live)})");
                    continue;
                }
                verified++;
            }
            catch (Exception ex) { failed++; errors.Add($"{op.Entry}: {ex.Message}"); }
        }
        var ack = new ProofAck
        {
            Ok = failed == 0, Verified = verified, Failed = failed,
            Error = string.Join(" | ", errors.Take(5)),
        };
        AgentState.Log($"proof: {verified} verified, {failed} failed" + (ack.Ok ? "" : " — " + ack.Error));
        return ack;
    }

    static string FirstDiff(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return $"@0x{i:X}: {a[i]:X2}!={b[i]:X2}";
        return "@len";
    }
}
