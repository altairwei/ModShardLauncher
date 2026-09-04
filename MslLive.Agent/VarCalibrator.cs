using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>变量符号 id 活体校准。设计依据（Task 11 设计影响行）：VarsMsg 模拟表有
/// 静态不可解的残余偏移（runner 编译序 ≠ CODE 文件序），不能作唯一真源；proof entry 的
/// 指令流与载荷 SemInstruction 同序——逐条走查到变量引用指令，读回活 buffer 里
/// <b>已解析</b>的操作数，建 name→真实 id 表。校准值永远是真理；与模拟表不一致只记日志
/// （drift 是预期）。形态校验失败（顶字节 ≠ RefTop / 内置 ≠ smallId / 非内置 low24 &lt; 100000）
/// = 操作数模型假设错了 → 该 op proof 失败，fail-closed。</summary>
public static class VarCalibrator
{
    static readonly Dictionary<string, int> map = new();

    public static IReadOnlyDictionary<string, int> Map => map;

    internal static void ResetForTest() => map.Clear();

    /// <summary>从 op 的活 buffer 收割变量 id。返回 false = 有形态错误（errors 里有明细）；
    /// 收割成功的名字进 map（重复收割同名同 id，last-wins 无害）。</summary>
    public static bool Harvest(OpMsg op, byte[] live, List<string> errors)
    {
        int before = errors.Count;
        int off = 0;
        foreach (var sem in op.Instructions)
        {
            int size;
            try { size = BcEncoder.ByteSize(sem); }
            catch (Exception ex) { errors.Add($"{op.Entry}: #{off:X} {ex.Message}"); return false; }
            if (off + size > live.Length)
            {
                errors.Add($"{op.Entry}: instruction stream overruns live buffer at 0x{off:X}");
                return false;
            }
            if (sem.Var != null)
                HarvestOne(op.Entry, sem, BitConverter.ToUInt32(live, off + 4), errors);
            else if (sem.Fn != null && size >= 8)   // call.i/push.i 函数引用：脚本 id 校准（CallCalibrator）
                CallCalibrator.HarvestOne(op.Entry, sem, BitConverter.ToUInt32(live, off + 4), errors);
            off += size;
        }
        if (off != live.Length)
        {
            errors.Add($"{op.Entry}: stream {off}B != live {live.Length}B");
            return false;
        }
        return errors.Count == before;
    }

    static void HarvestOne(string entry, SemInstruction sem, uint operand, List<string> errors)
    {
        byte top = (byte)(operand >> 24);
        uint low = operand & 0xFFFFFF;
        if (top != sem.RefTop)
        {
            errors.Add($"{entry}: var {sem.Var} live top byte 0x{top:X2} != payload RefTop 0x{sem.RefTop:X2}");
            return;
        }
        if (BuiltinVars.TryGetId(sem.Var!, out int small))   // 调用点已保证 Var != null
        {
            // 内置 = raw smallId（无 100000 偏置，S2 黄金实测）——读回值必须与 exe 表一致
            if (low != (uint)small)
                errors.Add($"{entry}: builtin {sem.Var} live low24 {low} != BuiltinVars {small}");
            return;
        }
        if (low < 100000)
        {
            errors.Add($"{entry}: var {sem.Var} live operand unresolved (0x{operand:X8})");
            return;
        }
        string key = Translator.KeyFor(sem.Inst, sem.Var!);
        int id = (int)low - 100000;
        map[key] = id;
        if (AgentState.VarMap?.TryGetValue(key, out int sim) == true && sim != id)
            AgentState.Log($"calibration drift: {key} simulated {sim} -> live {id}");
    }
}
