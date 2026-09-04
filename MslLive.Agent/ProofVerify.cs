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
                // 节点→活字节解析与 calib 语料同源（TryCorpusLive）：wrapper 裸根回退（#17/#22）、
                // 子条目直名取子区段（ProofBuilder 不过滤子条目——E2E seed 首证语料必含 gml_Script_*）
                if (!ApplyEngine.TryCorpusLive(op, out var node, out var live, out string why))
                { failed++; errors.Add($"{op.Entry}: {why}"); continue; }

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
            if (a[i] != b[i])
            {
                // 取证窗（E2E proof 断点诊断）：首差异前后 16 字节并排——单字节报告分不清
                // 「100000+0（..A0 86 01 00）vs raw FUNC 160（..A0 00 00 00）」这类操作数空间之争
                int s = Math.Max(0, i - 8), e = Math.Min(a.Length, i + 8);
                return $"@0x{i:X} enc[{Hex(a, s, e)}] != live[{Hex(b, s, e)}]";
            }
        return "@len";
    }

    static string Hex(byte[] x, int s, int e) =>
        string.Join(' ', Enumerable.Range(s, e - s).Select(j => j < x.Length ? x[j].ToString("X2") : "??"));
}
