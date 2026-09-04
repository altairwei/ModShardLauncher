using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>脚本调用 id 活体校准。E2E seed 首证（09-05 00:40 沙箱）：脚本调用操作数
/// （call.i / push.i 函数引用）= 100000+<b>脚本表序</b>，而非 100000+node+0x88（CODE chunk
/// 索引）。S2 ② 的「100000+codeId」四重验证在真机上成立是巧合——StoneShard 的 CODE chunk
/// 以脚本序打头（脚本 i 的 CODE 索引 == SCPT 表序），两空间重合无法区分；seed 打破重合
/// （scr_e2e_probe = CODE[2] 但 SCPT[0]；观察者活体操作数 0x186A0 = 100000+0，且
/// e2e_probe_result.txt = 111 证明该操作数真的解析到了探针——排除 raw FUNC 索引假设）。
/// 修法与 #21 变量校准同源：活体值是真理——proof/CalibOps 语料里每条 Fn 引用从活 buffer
/// 读回真实 id 建 fn→id 表；Translator.ResolveCall 查序 = registry → 本表 → node+0x88 兜底
/// （真机无语料命中时 +0x88 因 CODE 序巧合仍正确——保底不回归；两值都知且不等时记 drift）。</summary>
public static class CallCalibrator
{
    static readonly Dictionary<string, int> map = new();

    public static IReadOnlyDictionary<string, int> Map => map;

    internal static void ResetForTest() => map.Clear();

    /// <summary>单条 Fn 引用收割（VarCalibrator.Harvest 走查到 Fn sem 时调用，偏移对齐由
    /// 调用方保证；只收操作数 ≥8B 的形态——call.i/push.i，4B 的 callv 无操作数）。
    /// 内置/原生 = 注册表 raw 索引（无偏置，读回值必须与注册表一致——顺带 fail-closed
    /// 验证注册表本身）；脚本 = 100000+脚本表序。形态错 → errors（fail-closed）。</summary>
    public static void HarvestOne(string entry, SemInstruction sem, uint operand, List<string> errors)
    {
        string fn = sem.Fn!;
        uint low = operand & 0xFFFFFF;
        int reg = Registry.IndexOf(fn);
        if (reg >= 0)
        {
            if (low != (uint)reg)
                errors.Add($"{entry}: call {fn} live low24 {low} != registry {reg}");
            return;
        }
        if ((operand >> 24) != 0)
        { errors.Add($"{entry}: call {fn} live operand 0x{operand:X8} has nonzero top byte (script ref must be 0x00-prefixed)"); return; }
        if (low < 100000)
        { errors.Add($"{entry}: call {fn} live operand unresolved (0x{operand:X8})"); return; }
        int id = (int)low - 100000;
        map[fn] = id;
        // drift 取证：+0x88（CODE 索引）≠ 活体（脚本表序）= 两空间分离形态（真机重合时不记）
        if (NodeIndex.TryGet(fn, out var n) && n.CodeId != (uint)id)
            AgentState.Log($"call calibration drift: {fn} node+0x88 {n.CodeId} -> live {id}");
    }
}
