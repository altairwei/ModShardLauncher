using System.Collections.Generic;
using System.Linq;

namespace ModShardLauncher.HotReload;

/// <summary>loader GML 生成器（spec §7.3/§7.4）。生成的是文本，由 MSL 侧 ReplaceGML 对
/// boot baseline 快照编译。**硬约束：loader 文本里出现的字符串字面量必须全部已在 boot STRG**
/// （运行时字符串池装载期冻结，S2③；agent 对非 boot 字符串 fail-closed）——所以精灵路径不写
/// 字面量，用 Task 8 预播种的 "mods/_live/res/" + string(索引) + ".png" 运行时拼接；
/// PNG 文件名 = 运行时索引（Task 4 按同一约定写盘）。名字注册表（global.msl_f/msl_assets/msl_obj）
/// 已删除：热代码的全部引用在 MSL overlay 里烘焙成整数/槽名，GML 侧没有按名查询的消费者（YAGNI，
/// 见 v1 取舍清单）。</summary>
public static class LoaderGen
{
    /// <summary>精灵热更三段式（spec §7.3，StoneShard 原生组合：add→assign→delete）。
    /// 新精灵与改精灵同一形态——新精灵的 blank 索引处已被 GameStart 填了 _blank.png，
    /// assign 直接覆盖内容，索引不变。
    /// 路径拼接用 <b>裸数字</b>而非 string(字面量)：UTMT 编译器会把
    /// "a" + string(字面量) + "b" 常量折叠成单个非 boot 整路径字面量（fix-loop #16
    /// fold-probe A 实证 → agent 非 boot 字符串拒绝）；"a" + 数字 + "b" 不折叠
    /// （fold-probe D：两个种子串 + 运行时 add，且无 string() 调用、局部数仅 _t）。</summary>
    public static string SpriteLoader(SpriteChange c, StripInfo strip, int runtimeIndex) =>
        $"// sprite {c.Name} -> {runtimeIndex}\n" +
        $"var _t = sprite_add(\"mods/_live/res/\" + {runtimeIndex} + \".png\", {strip.Frames}, false, false, {strip.OriginX}, {strip.OriginY});\n" +
        $"sprite_assign({runtimeIndex}, _t);\n" +
        $"sprite_delete(_t);\n";

    /// <summary>壳对象配置：可选 sprite 属性（spec §6.4 对象行）。壳索引与精灵索引都是
    /// overlay 烘焙的整数常量；无 sprite → 空串（该壳本批不需要配置）。</summary>
    public static string ShellConfig(string objName, int shellRuntimeIndex, int? spriteRuntimeIndex) =>
        spriteRuntimeIndex is int si
            ? $"// shell {objName} -> {shellRuntimeIndex}\nobject_set_sprite({shellRuntimeIndex}, {si});\n"
            : "";

    /// <summary>一批次的全部 loader 片段按序合并（顺序规则 spec §7.4：精灵 → 壳配置）。</summary>
    public static string MergeOrdered(IEnumerable<string> fragments) =>
        "// msl live loader (generated)\n" + string.Join('\n', fragments.Where(f => f.Length > 0));
}
