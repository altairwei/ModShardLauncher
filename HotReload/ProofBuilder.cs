using System.Collections.Generic;
using System.Linq;
using MslLive.Shared;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public static class ProofBuilder
{
    /// <summary>握手编码自证（Task 14：agent 编码后必须活内存 AOB 唯一命中且地址==执行记录+0x18）
    /// 的载荷选取。特征 = opcode Kind × {字符串引用, 函数引用(call/push.v), 变量作用域(i/g/builtin),
    /// 分支}。先轻扫（不提取）全部 entry 收集特征，贪心挑最小覆盖集（上限 k），
    /// 再对入选 entry 跑完整 RefsExtractor（AssetRanges.Empty——baseline 无新资产区间，
    /// 任何字面量都不会触发歧义，提取不可能失败）。字符串索引用 strgIndex 填。</summary>
    public static ProofMsg Build(UndertaleData boot, Dictionary<string, int> strgIndex, int k = 64)
    {
        var resolver = new AssetKindResolver();
        var picked = new List<UndertaleCode>();
        var coveredKinds = new HashSet<byte>();
        var seenFeatures = new HashSet<string>();
        int scannedSinceNew = 0;
        foreach (var code in boot.Code)
        {
            if (picked.Count >= k || scannedSinceNew > 4000) break;
            var (kinds, features) = ScanFeatures(code);
            bool addsKind = kinds.Any(t => coveredKinds.Add(t));
            bool addsFeature = features.Any(f => seenFeatures.Add(f));
            if (addsKind || addsFeature)
            {
                picked.Add(code);
                scannedSinceNew = 0;
            }
            else scannedSinceNew++;
        }

        var msg = new ProofMsg();
        foreach (var code in picked)
        {
            var payload = RefsExtractor.Extract(code, boot, AssetRanges.Empty, resolver);
            var op = new OpMsg
            {
                Seq = msg.Ops.Count, Kind = "swap", Entry = code.Name.Content,
                LocalsCount = (int)code.LocalsCount,
                Instructions = payload.Instructions,
                Variables = payload.Variables,
                Functions = payload.Functions,
            };
            foreach (var s in payload.Strings)
                op.Strings.Add(new StrRef { Content = s, StrgIndex = strgIndex.GetValueOrDefault(s, -1) });
            msg.Ops.Add(op);
        }
        return msg;
    }

    /// <summary>轻扫指令 Kind/引用形态（不重提取）——字段访问与 InstructionVars /
    /// RefsExtractor.ToSem 逐字相同（同一 vendored 类型）。特征键 = Kind 限定
    /// （"Push:str"、"Call:fn"、"Pop:var:i"），分支形态由 Kind 本身覆盖（B/Bt/Bf/PushEnv/PopEnv）。
    /// 变量作用域判据与 VarIdSimulator 同源（VARI 条目 VarID/InstanceType + 指令 TypeInst）。</summary>
    static (HashSet<byte> Kinds, HashSet<string> Features) ScanFeatures(UndertaleCode code)
    {
        var kinds = new HashSet<byte>();
        var features = new HashSet<string>();
        foreach (var inst in code.Instructions)
        {
            kinds.Add((byte)inst.Kind);
            if (inst.Value is UndertaleResourceById<UndertaleString, UndertaleChunkSTRG>)
                features.Add(inst.Kind + ":str");
            if (inst.Function?.Target != null || inst.Value is UndertaleInstruction.Reference<UndertaleFunction>)
                features.Add(inst.Kind + ":fn");
            if (InstructionVars.TryGet(inst, out string? _, out short instType, out var vtarget))
            {
                string scope =
                    instType == (short)UndertaleInstruction.InstanceType.Arg ? "builtin" :   // argumentN 实测 smallId
                    instType == (short)UndertaleInstruction.InstanceType.Local ? "local" :
                    vtarget!.VarID == -6 ? "builtin" :
                    (short)vtarget.InstanceType == (short)UndertaleInstruction.InstanceType.Global ? "g" : "i";
                features.Add(inst.Kind + ":var:" + scope);
            }
        }
        return (kinds, features);
    }
}
