using System;
using System.Collections.Generic;
using System.Linq;
using MslLive.Shared;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public sealed class CorpusResult
{
    public List<OpMsg> Ops { get; } = new();
    /// <summary>无 baseline 来源的 (op 下标, 键)——调用方据此诚实拒批（#21 fail-closed）。</summary>
    public List<(int OpIndex, string Key)> Missing { get; } = new();
}

/// <summary>#21 校准语料选取（_ally_hp 事故修复的 MSL 半侧）：变量 id 的唯一真源是 runner
/// 装载时回填进活 buffer 的操作数——模拟表（VarIdSimulator）有静态不可解残余偏移，只能作
/// drift 诊断。本类为本批 ops 引用的每个非内置变量键在 boot baseline 里找一个语料 entry，
/// agent 装表前对其活 buffer 跑 VarCalibrator.Harvest 建 name→真 id 表。
///
/// 语料约束：只收 ParentEntry==null 的根条目（子条目流起点与共享 buffer 基址错位 4B，
/// walk 会错位）；同键多源优先 gml_Object_ 事件条目（直名节点命中率高——gml_GlobalScript_
/// 根在 NodeIndex 无直名节点，agent 侧须回退 gml_Script_ 子名拿共享基址）。
/// l: 键域严格匹配，i:↔g: 互认（与 Translator 的回落规则同源）。
/// 全新变量名（baseline 无来源）→ Missing——runner 才会为它分配新 id，我们无安全分配路径，
/// 诚实拒绝（重启游戏后由正常载入覆盖）。</summary>
public static class CalibCorpus
{
    /// <summary>键域规则与 Translator.KeyFor 同源：指令 TypeInst -5(Global)→"g:"，-7(Local)→"l:"，其余→"i:"。</summary>
    public static string KeyFor(short instType, string name) =>
        (instType == -5 ? "g:" : instType == -7 ? "l:" : "i:") + name;

    public static CorpusResult Build(UndertaleData boot, IReadOnlyList<OpMsg> ops, AssetKindResolver resolver)
    {
        var result = new CorpusResult();

        // 1. 需求集：逐 op 收集非内置变量键（内置 = exe 固定 smallId 表，不需要语料）
        var needed = new List<HashSet<string>>(ops.Count);
        var remaining = new HashSet<string>();
        foreach (var op in ops)
        {
            var keys = new HashSet<string>();
            foreach (var sem in op.Instructions)
            {
                if (sem.Var == null || BuiltinVars.Map.ContainsKey(sem.Var)) continue;
                string k = KeyFor(sem.Inst, sem.Var);
                keys.Add(k);
                remaining.Add(k);
            }
            needed.Add(keys);
        }
        if (remaining.Count == 0) return result;

        // 2. baseline 扫描建来源（早退：全部键有着落即停）。同出现一并覆盖兄弟域键
        //    （收割经 Translator i↔g 回落命中）；"l:" 只认 l: 出现（回落会串 id——845 实证）。
        var sourceFor = new Dictionary<string, (string Entry, bool IsObjectEvent)>();
        var codeByName = new Dictionary<string, UndertaleCode>();
        foreach (var code in boot.Code)
        {
            if (remaining.Count == 0) break;
            if (code.ParentEntry != null) continue;
            string entryName = code.Name.Content;
            bool isObjectEvent = entryName.StartsWith("gml_Object_", StringComparison.Ordinal);
            bool useful = false;
            foreach (var inst in code.Instructions)
            {
                if (!InstructionVars.TryGet(inst, out string? name, out short instType, out _)) continue;
                if (BuiltinVars.Map.ContainsKey(name!)) continue;
                string k = KeyFor(instType, name!);
                useful |= Cover(k, entryName, isObjectEvent);
                if (k[0] == 'i') useful |= Cover("g:" + name, entryName, isObjectEvent);
                else if (k[0] == 'g') useful |= Cover("i:" + name, entryName, isObjectEvent);
            }
            if (useful) codeByName[entryName] = code;
        }

        // 3. 逐 op 核对覆盖；无来源键 → Missing（构建期诚实拒绝由 HotPipeline 收口）
        for (int i = 0; i < ops.Count; i++)
            foreach (var k in needed[i])
                if (!sourceFor.ContainsKey(k))
                    result.Missing.Add((i, k));

        // 4. 语料 entry 去重提取（RefsExtractor 与 ProofBuilder 同参数形态：baseline 无新资产区间）
        foreach (var entryName in sourceFor.Values.Select(v => v.Entry).Distinct())
        {
            var payload = RefsExtractor.Extract(codeByName[entryName], boot, AssetRanges.Empty, resolver);
            result.Ops.Add(new OpMsg
            {
                Seq = -1, Kind = "calib", Entry = entryName,
                LocalsCount = (int)codeByName[entryName].LocalsCount,
                Instructions = payload.Instructions,
                Variables = payload.Variables,
            });
        }
        return result;

        bool Cover(string key, string entryName, bool isObjectEvent)
        {
            if (!remaining.Contains(key)) return false;
            if (!sourceFor.TryGetValue(key, out var cur) || (!cur.IsObjectEvent && isObjectEvent))
                sourceFor[key] = (entryName, isObjectEvent);   // 同键多源：事件条目优先（直名节点命中率高）
            remaining.Remove(key);
            return true;
        }
    }
}
