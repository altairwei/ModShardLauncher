using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public static class VarIdSimulator
{
    /// <summary>S2 ②：实例/全局/局部变量的运行时操作数 = 顶字节 0xA0 | (100000+loadOrderId)，
    /// loadOrderId 按 runner 装载遇到顺序分配、名字去重、单一数值空间、≠VARI 序。
    ///
    /// <para><b>规则来源（Task 7 探针 v1–v8 + Task 11 exe 表交叉验证）：</b></para>
    /// - 扫描流 = CODE chunk 文件序逐 entry 逐指令的变量引用；首次遇到即分配（0 起）。
    /// - 局部变量（.localvar 声明族）与实例变量同空间（_borderLeft..Bottom 实测 100646..100649）。
    /// - 内置变量跳过：<b>判据 = 名字 ∈ exe 固定表（<see cref="MslLive.Shared.BuiltinVars"/>，218 条，
    ///   Task 11 实测注册序）</b>。Task 11 用该表对 vanilla 全轨迹做过双向交叉：被跳过的 95 个
    ///   相异名字全在表内、被分配的 19963 个名字零碰撞——名字判据与旧启发式
    ///   （VarID==-6 / TypeInst==-15）在 vanilla 上完全等价，且名字判据更贴近 runner 语义
    ///   （runner 按名字先查内置表）。旧启发式废弃。
    /// - 键域 = 目标 VARI 条目 InstanceType == -5 → "g:"，-7(Local) → "l:"，其余 → "i:"。
    ///   i/g 共享同一计数器（实测邻接：spr=1215、waterDrawState=1216、scr_unitRenderDrawSprite=1217）。
    ///   局部独立成 "l:"（fix-loop #16 [V] 实证：vanilla 845 个 Local+非 Local VARI 并存
    ///   ——target[Self,Global,Local] 族；与 "i:" 同键会串 id。与 Translator.KeyFor 同源）。
    ///
    /// <para><b>已知偏差（Task 11 再调查后的诚实结论）：</b>绝对值系统性偏移仍存在
    /// （早窗 -3、(_color,_borderLeft) 窗 +1，净 -2 @646..649 锚点）。Task 11 用 exe 表
    /// 证伪了「内置误分类」假说（两窗口零分歧），Kind 直方图证伪了「提取遗漏」假说
    /// （miss 全是 push.i/push.s 字面量与函数引用），65 个双上下文名证伪了「按名单去重」假说
    /// （runtime 同样按上下文双计）。残余偏移只能是 runner 编译顺序与 CODE 文件序的细微差别
    /// （约 4 个名字跨窗口）——静态不可解，<b>绝对基准 646..649 断言不恢复</b>；
    /// 精确化走 Task 14 agent 活体自校准（proof 逐条读回真实操作数建表），本表作交叉校验基线。</para>
    ///
    /// <para><b>S2 修正</b>：findings-s2 行 65「0x80=全局作用域标记」系误读——该数据点
    /// （pop.v.v [stacktop]self.X）的 0x80 是 VariableType.Stacktop 引用形态字节
    /// （vendored UTMT UndertaleCode.cs:298 ReferenceType = 操作数顶字节 &amp; 0xF8），
    /// 非作用域。真全局引用（TypeInst=-5）的顶字节编码由 Task 11 Step 5 活体收割。</para>
    ///
    /// <para><b>最终背书不变</b>：Task 14 的 AOB 编码自证逐字节对活内存校验——模拟若错，
    /// 自证拒会话（fail-closed）。</para></summary>
    public static Dictionary<string, int> Simulate(UndertaleData boot)
    {
        var ids = new Dictionary<string, int>();
        int next = 0;
        foreach (var code in boot.Code)
        foreach (var instr in code.Instructions)
        {
            if (!InstructionVars.TryGet(instr, out string? name, out _, out var target)) continue;
            if (MslLive.Shared.BuiltinVars.Map.ContainsKey(name!))
                continue;   // 内置：exe 固定 smallId 表（Task 11 实测 218 条），不占装载序 id 空间
            string key = ((short)target!.InstanceType == (short)UndertaleInstruction.InstanceType.Global ? "g:"
                : (short)target!.InstanceType == (short)UndertaleInstruction.InstanceType.Local ? "l:"
                : "i:") + name;
            if (!ids.ContainsKey(key)) ids[key] = next++;
        }
        return ids;
    }
}

/// <summary>变量引用指令的轻量读取——字段访问路径与 Task 2 RefsExtractor 逐字相同
/// （spike RefsExtractor.cs ToSem：名字走 Value 的 Reference&lt;UndertaleVariable&gt;（push.v）
/// 或 Destination.Target（pop.v），InstanceType 读 TypeInst）。返回 false = 非变量引用指令。
/// target 一并带出（VarIdSimulator 的内置判据需要 VARI 条目的 VarID/InstanceType）。</summary>
public static class InstructionVars
{
    public static bool TryGet(UndertaleInstruction instr,
        out string? name, out short instType, out UndertaleVariable? target)
    {
        target = (instr.Value as UndertaleInstruction.Reference<UndertaleVariable>)?.Target
              ?? instr.Destination?.Target;
        name = target?.Name?.Content;
        instType = (short)instr.TypeInst;
        return name != null;
    }
}
