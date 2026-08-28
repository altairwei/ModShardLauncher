using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public static class VarIdSimulator
{
    /// <summary>S2 ②：实例/全局/局部变量的运行时操作数 = 顶字节 0xA0 | (100000+loadOrderId)，
    /// loadOrderId 按 runner 装载遇到顺序分配、名字去重、单一数值空间、≠VARI 序。
    ///
    /// <para><b>规则来源（Task 7 执行期探针 v1–v8，对 s2/runtime/run.hex 全量 65 操作数逐一对齐）：</b></para>
    /// - 扫描流 = CODE chunk 文件序逐 entry 逐指令的变量引用；首次遇到即分配（0 起）。
    /// - 局部变量（.localvar 声明族）与实例变量同空间（_borderLeft..Bottom 是 scr_unitRenderDrawSprite
    ///   的 local，实测 100646..100649）；指令 TypeInst=-7 不跳过。
    /// - 内置变量跳过：判据 = 引用目标 VARI 条目 <c>VarID == -6</c>（exe 固定表标记；sprite_index/
    ///   x/room 等实测 smallId 26/…），或指令 TypeInst == -15（Arg 形态；argument0 实测 smallId 0x5D）。
    ///   名字表（gamemaker.json）不可用——它只收 11 个 Asset.* 类型变量，image_index/x 等根本不在内。
    /// - 键域 = 目标 VARI 条目 InstanceType == -5 → "g:"，其余 → "i:"。i/g 共享同一计数器
    ///   （实测邻接：spr=1215、waterDrawState=1216、g:scr_unitRenderDrawSprite=1217）。
    ///
    /// <para><b>已知偏差（未消化，禁止靠改期望凑绿）：</b>本模型对 13 个地面真值点的 12 个
    /// 逐点间距全部精确复现，但绝对值有系统性偏移：最早窗口少 3 名、(_color, _borderLeft) 窗口
    /// 多 1 名——共 4 个未识别名字（疑似 exe 内置表的真实成员集与 VARI 标记不完全重合，
    /// 如 roomNext 的 VarID=41518 无内置标记却疑似内置；或静态变量 Inst=-16 形态的特殊处理）。
    /// 精确化需要 Task 11 的 exe 内置变量表收割 + 运行实例复测。</para>
    ///
    /// <para><b>S2 修正</b>：findings-s2 行 65「0x80=全局作用域标记」系误读——该数据点
    /// （pop.v.v [stacktop]self.X）的 0x80 是 VariableType.Stacktop 引用形态字节
    /// （vendored UTMT UndertaleCode.cs:298 ReferenceType = 操作数顶字节 &amp; 0xF8），
    /// 非作用域。真全局引用（TypeInst=-5）的顶字节编码尚无地面真值 → Task 11 收割项。</para>
    ///
    /// <para><b>最终背书不变</b>：Task 14 的 AOB 编码自证逐字节对活内存校验——模拟若错，
    /// 自证拒会话（fail-closed）。探针证明偏移存在，意味着 Task 14 不能把本表当唯一真源；
    /// agent 侧从活内存自校准（proof entry 的指令与 SemInstruction 同序，可逐个读回真实操作数）
    /// 是推荐补救，见 Task 7 完成记录与 plan 偏差说明。</para></summary>
    public static Dictionary<string, int> Simulate(UndertaleData boot)
    {
        var ids = new Dictionary<string, int>();
        int next = 0;
        foreach (var code in boot.Code)
        foreach (var instr in code.Instructions)
        {
            if (!InstructionVars.TryGet(instr, out string? name, out _, out var target)) continue;
            if (target!.VarID == -6 || (short)instr.TypeInst == (short)UndertaleInstruction.InstanceType.Arg)
                continue;   // 内置：exe 固定 smallId 表，不占装载序 id 空间
            string key = ((short)target.InstanceType == (short)UndertaleInstruction.InstanceType.Global ? "g:" : "i:") + name;
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
