using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public sealed record ChangedEntry(string Name, UndertaleCode Baseline, UndertaleCode Product);

public static class CodeDiffer
{
    /// <summary>按名字配对的 entry 级 diff。baseline-only / product-only entry 不在本 spike 范围
    ///（v1 热更只替换既有 entry；纯新增 entry 属主实现的槽位池机制，CLI 侧另行计数报告）。</summary>
    public static List<ChangedEntry> Diff(UndertaleData baseline, UndertaleData product)
    {
        var changed = new List<ChangedEntry>();
        var productByName = new Dictionary<string, UndertaleCode>();
        foreach (var p in product.Code)
            if (!productByName.ContainsKey(p.Name.Content))
                productByName.Add(p.Name.Content, p);
        foreach (var b in baseline.Code)
        {
            if (!productByName.TryGetValue(b.Name.Content, out var p)) continue;
            if (!SameInstructions(b, p))
                changed.Add(new ChangedEntry(b.Name.Content, b, p));
        }
        return changed;
    }

    public static bool SameInstructions(UndertaleCode b, UndertaleCode p)
    {
        if (b.Instructions.Count != p.Instructions.Count) return false;
        for (int i = 0; i < b.Instructions.Count; i++)
            if (!InstructionEqual(b.Instructions[i], p.Instructions[i])) return false;
        return true;
    }

    /// <summary>语义比较：结构字段全比；操作数按类——字面量按值、变量按名字+Type、函数按名字、字符串按内容。
    /// 不比较 Address（位置不是内容，ReplaceGML 后必变）。</summary>
    public static bool InstructionEqual(UndertaleInstruction a, UndertaleInstruction b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Type1 != b.Type1 || a.Type2 != b.Type2 || a.TypeInst != b.TypeInst) return false;
        if (a.ArgumentsCount != b.ArgumentsCount) return false;
        if (a.JumpOffset != b.JumpOffset || a.JumpOffsetPopenvExitMagic != b.JumpOffsetPopenvExitMagic) return false;
        if (a.ComparisonKind != b.ComparisonKind) return false;
        if (a.SwapExtra != b.SwapExtra) return false;
        if (!OperandEqual(a.Value, b.Value)) return false;
        if (RefName(a.Destination) != RefName(b.Destination)) return false;
        if (RefTypeFlag(a.Destination) != RefTypeFlag(b.Destination)) return false;
        if (RefName(a.Function) != RefName(b.Function)) return false;
        return true;
    }

    static bool OperandEqual(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        if (x.Equals(y)) return true; // 装箱数值字面量按值比较
        if (x is UndertaleInstruction.Reference<UndertaleVariable> vx && y is UndertaleInstruction.Reference<UndertaleVariable> vy)
            return vx.Type == vy.Type && RefName(vx) == RefName(vy);
        if (x is UndertaleInstruction.Reference<UndertaleFunction> fx && y is UndertaleInstruction.Reference<UndertaleFunction> fy)
            return RefName(fx) == RefName(fy);
        if (x is UndertaleResourceById<UndertaleString, UndertaleChunkSTRG> sx && y is UndertaleResourceById<UndertaleString, UndertaleChunkSTRG> sy)
            return sx.Resource?.Content == sy.Resource?.Content;
        return false;
    }

    static string RefName(UndertaleInstruction.Reference<UndertaleVariable>? r) => r?.Target?.Name?.Content ?? "";
    static string RefName(UndertaleInstruction.Reference<UndertaleFunction>? r) => r?.Target?.Name?.Content ?? "";
    static int RefTypeFlag(UndertaleInstruction.Reference<UndertaleVariable>? r) => (int?)r?.Type ?? -1;
}
