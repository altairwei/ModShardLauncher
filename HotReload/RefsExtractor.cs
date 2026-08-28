using System;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;
using MslLive.Shared;

namespace ModShardLauncher.HotReload;

public sealed class AssetAmbiguityException : Exception
{
    public string Entry { get; }
    public int InstructionIndex { get; }
    public long Literal { get; }

    public AssetAmbiguityException(string entry, int idx, long literal, string reason)
        : base($"[{entry}] instruction #{idx}: literal {literal} in new-asset range but {reason}")
    {
        Entry = entry;
        InstructionIndex = idx;
        Literal = literal;
    }
}

public static class RefsExtractor
{
    /// <summary>从一个 product entry 提取 SwapCode 语义载荷。新增区间内定不了类的字面量 → AssetAmbiguityException（整批拒绝）。</summary>
    public static SwapCodePayload Extract(UndertaleCode code, UndertaleData data, AssetRanges ranges, AssetKindResolver resolver)
    {
        var payload = new SwapCodePayload { Entry = code.Name.Content };
        var vars = new HashSet<string>();
        var fns = new HashSet<string>();
        var strs = new HashSet<string>();
        var sems = new List<SemInstruction>(code.Instructions.Count);
        var assets = new Dictionary<string, AssetRef>();
        var tracker = new StackTracker(code.Instructions);

        for (int idx = 0; idx < code.Instructions.Count; idx++)
        {
            var inst = code.Instructions[idx];
            tracker.Before(inst);
            ResolveAssetContexts(code, idx, tracker, ranges, resolver, data, sems, assets);
            sems.Add(ToSem(inst, vars, fns, strs));
            tracker.Apply(inst, idx);
        }

        // 尾扫（spec §5.1「查不到 → 整批拒绝」的收口）：新增区间内的整型字面量若从未被任何
        // 消费者标注（栈区被毒化 / 消费者形态不在识别集内 / 三元选择等），不做静默恒等翻译——
        // 那会把它错当成普通常量发出去，运行时错位引用。一律拒绝。
        for (int idx = 0; idx < code.Instructions.Count; idx++)
        {
            long? lit = LiteralOf(code.Instructions[idx]);
            if (lit == null || ranges.CandidateKinds(lit.Value).Count == 0) continue;
            if (sems[idx].AssetKinds is not { Count: > 0 })
                throw new AssetAmbiguityException(code.Name.Content, idx, lit.Value,
                    "literal in new-asset range was never annotated by any consumer (poisoned stack region / unknown consumer)");
        }

        payload.Instructions = sems;
        payload.Variables = vars.ToList();
        payload.Functions = fns.ToList();
        payload.Strings = strs.ToList();
        payload.Assets = assets.Values.ToList();
        return payload;
    }

    /// <summary>整型字面量（pushi.e 的 short / push.e 的 int / long）。</summary>
    public static long? LiteralOf(UndertaleInstruction inst) => inst.Value switch
    {
        short s => s,
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>消费者驱动定类：call 参数 / pop 目标 / pushenv。producer 是区间内整型字面量时标注其种类。</summary>
    static void ResolveAssetContexts(UndertaleCode code, int idx, StackTracker tracker, AssetRanges ranges,
        AssetKindResolver resolver, UndertaleData data, List<SemInstruction> sems, Dictionary<string, AssetRef> assets)
    {
        var inst = code.Instructions[idx];
        switch (inst.Kind)
        {
            case UndertaleInstruction.Opcode.Call:
            {
                var fn = inst.Function?.Target?.Name?.Content;
                if (fn == null) break;
                var producers = tracker.Top(inst.ArgumentsCount);
                // GMS VM 参数逆序压栈：Top 的 bottom-first 列表里 index j 对应源码参数 argc-1-j（arg0 在栈顶）
                for (int j = 0; j < producers.Count; j++)
                {
                    int srcSlot = producers.Count - 1 - j;
                    TryAnnotate(code, idx, producers[j], resolver.KindForCallArg(fn, srcSlot), ranges, data, sems, assets, $"call {fn} arg{srcSlot}");
                }
                break;
            }
            case UndertaleInstruction.Opcode.Pop when inst.Type1 == UndertaleInstruction.DataType.Variable:
            {
                var v = inst.Destination?.Target?.Name?.Content;
                if (v == null) break;
                TryAnnotate(code, idx, tracker.Top(1).FirstOrDefault(), resolver.KindForVariable(v), ranges, data, sems, assets, $"assignment to {v}");
                break;
            }
            case UndertaleInstruction.Opcode.PushEnv:
            {
                TryAnnotate(code, idx, tracker.Top(1).FirstOrDefault(), resolver.KindForPushEnv(), ranges, data, sems, assets, "pushenv target");
                break;
            }
        }
    }

    static void TryAnnotate(UndertaleCode code, int consumerIdx, int? producerIdx, AssetKind? contextKind,
        AssetRanges ranges, UndertaleData data, List<SemInstruction> sems, Dictionary<string, AssetRef> assets, string where)
    {
        if (producerIdx == null || contextKind == null)
        {
            // 无上下文但字面量在新增区间 → 无法区分「新资产引用」与「普通常量」→ 整批拒绝
            if (producerIdx != null && contextKind == null)
            {
                long? lit = LiteralOf(code.Instructions[producerIdx.Value]);
                if (lit != null && ranges.CandidateKinds(lit.Value).Count > 0)
                    throw new AssetAmbiguityException(code.Name.Content, producerIdx.Value, lit.Value, $"no kind context ({where})");
            }
            return;
        }
        var producer = code.Instructions[producerIdx.Value];
        long? l = LiteralOf(producer);
        if (l == null) return;
        var candidates = ranges.CandidateKinds(l.Value);
        if (candidates.Count == 0) return; // baseline 区间或普通常量：恒等翻译，无需标注
        if (!candidates.Contains(contextKind.Value))
            throw new AssetAmbiguityException(code.Name.Content, producerIdx.Value, l.Value,
                $"context says {contextKind} but candidate ranges are [{string.Join(", ", candidates)}] ({where})");

        var sem = sems[producerIdx.Value];
        sem.AssetKinds ??= new List<string>();
        if (sem.AssetKinds.Count > 0 && !sem.AssetKinds.Contains(contextKind.ToString()))
            throw new AssetAmbiguityException(code.Name.Content, producerIdx.Value, l.Value,
                $"conflicting kinds {string.Join("+", sem.AssetKinds)} vs {contextKind} ({where})");
        if (!sem.AssetKinds.Contains(contextKind.ToString()))
        {
            sem.AssetKinds.Add(contextKind.ToString());
            string key = $"{contextKind}:{l}";
            if (!assets.ContainsKey(key))
                assets[key] = new AssetRef { Kind = contextKind.ToString(), Index = l.Value, Name = AssetRanges.AssetName(data, contextKind.Value, l.Value) };
        }
    }

    static SemInstruction ToSem(UndertaleInstruction inst, HashSet<string> vars, HashSet<string> fns, HashSet<string> strs)
    {
        var sem = new SemInstruction
        {
            Kind = (byte)inst.Kind,
            T1 = (byte)inst.Type1,
            T2 = (byte)inst.Type2,
            Inst = (short)inst.TypeInst,
            Low16 = inst.Kind switch
            {
                UndertaleInstruction.Opcode.PushI => (ushort)Convert.ToInt16(inst.Value ?? 0),
                UndertaleInstruction.Opcode.Call => inst.ArgumentsCount,
                UndertaleInstruction.Opcode.Cmp => (byte)inst.ComparisonKind,
                _ => inst.SwapExtra,
            },
            Jump = inst.Kind is UndertaleInstruction.Opcode.B or UndertaleInstruction.Opcode.Bt
                    or UndertaleInstruction.Opcode.Bf or UndertaleInstruction.Opcode.PopEnv
                    or UndertaleInstruction.Opcode.PushEnv   // pushenv 同样携带 24 位偏移（S2 黄金 buffer 实测 A00000BA）
                ? inst.JumpOffset : null,
            Cmp = inst.Kind == UndertaleInstruction.Opcode.Cmp ? (byte)inst.ComparisonKind : null,
        };
        switch (inst.Value)
        {
            case UndertaleInstruction.Reference<UndertaleVariable> v: sem.Var = v.Target?.Name?.Content; break;
            case UndertaleInstruction.Reference<UndertaleFunction> f: sem.Fn = f.Target?.Name?.Content; break;
            case UndertaleResourceById<UndertaleString, UndertaleChunkSTRG> s: sem.Str = s.Resource?.Content; break;
            case short sv: sem.Int = sv; break;
            case int iv: sem.Int = iv; break;
            case long lv: sem.Int = lv; break;
            case double dv: sem.Real = dv; break;
            case float fv: sem.Real = fv; break;
            case bool bv: sem.Int = bv ? 1 : 0; break;
        }
        if (inst.Destination?.Target != null) sem.Var = inst.Destination.Target.Name?.Content;
        if (inst.Function?.Target != null) sem.Fn = inst.Function.Target.Name?.Content;
        // 变量引用指令：文件操作数顶字节随载荷走——VM 加载只重写 low24，顶字节
        // （0xA0 Normal / 0x80 StackTop，vendored dll 的 Reference.Type）必须原样回填
        // （findings-t11 新发现 4：静态占位 0x?000DEAD 的顶字节在文件侧已就位）。
        if (sem.Var != null)
        {
            var r = inst.Value as UndertaleInstruction.Reference<UndertaleVariable> ?? inst.Destination;
            if (r != null) sem.RefTop = (byte)r.Type;
        }
        if (sem.Var != null) vars.Add(sem.Var);
        if (sem.Fn != null) fns.Add(sem.Fn);
        if (sem.Str != null) strs.Add(sem.Str);
        return sem;
    }
}
