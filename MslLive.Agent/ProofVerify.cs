using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>AOB 编码自证（握手 proof 信封的处理器）：对每个 proof op——
/// ① 节点链取活 buffer（执行记录 +0x18/+0x08）；② VarCalibrator 读回真实变量 id 并做形态校验；
/// ③ Translator+BcEncoder 独立编码；④ 双条件：命中点全等 ∧ 头窗全局唯一。
/// 计划原文的全 pattern ScanAob 调整为「全等直读 + min(96,len) 头窗唯一性」：
/// 全等直读在强度上 ⊇ AOB-at-hit；64 op × 全内存 712B 扫描是分钟级，窗口扫描毫秒级。
/// 短 buffer（&lt;48B）不查全局唯一性——return-0 级微 stub 的短 pattern 在相邻 buffer
/// 竞技场里必然多重命中，唯一性工具对它们不适用；节点链结构校验 + 全等直读已足够。</summary>
public static class ProofVerify
{
    const int UniquenessMinLen = 48;
    const int UniquenessWindow = 96;

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
                if (encoded.Length >= UniquenessMinLen)
                {
                    var window = encoded.Take(Math.Min(UniquenessWindow, encoded.Length)).Cast<byte?>().ToArray();
                    var hits = Mem.ScanAob(window);
                    if (hits.Count != 1 || hits[0] != node.BufPtr)
                    {
                        failed++;
                        errors.Add($"{op.Entry}: head-window hits={hits.Count}, expected unique @0x{node.BufPtr:X}");
                        continue;
                    }
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
