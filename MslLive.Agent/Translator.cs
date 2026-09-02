using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>IOperandResolver 的生产实现（S2 ② 规则表 + Task 11 统一编码模型）。
/// 每 op 构造一次（携带该 op 的 Strings/Assets 上下文）。
///
/// 变量 id 解析顺序：活体校准表（VarCalibrator——proof 阶段与 apply 前 CalibOps 语料从活
/// buffer 逐条读回的真实值）→ Reject。<b>#21 起模拟表兜底删除</b>（_ally_hp 事故：
/// Task 11 已证模拟有静态不可解残余偏移，00:05 真机 20 个未校准变量全走兜底 →
/// Magic_Power 模拟 2269 写成活体 102269=_ally_hp。「兜底+记日志」= 用户看不见的静默
/// 错值；此类路径必须 fail-closed——VarIdSimulator 仅留作 VarCalibrator 的 drift 诊断）。
/// 内置变量（exe 固定 smallId 表）优先于符号表——名字判据与 runner 语义同源（Task 11 Step 4/5）。
///
/// 键域与 VarIdSimulator 同源：指令 TypeInst == -5(Global) → "g:"，-7(Local) → "l:"，
/// 其余 → "i:"。局部独立成域（fix-loop #16 [V] 实证：vanilla 845 个 Local+非 Local VARI
/// 并存——key/state/i/j/target[Self,Global,Local]…，-7 落 "i:" 与实例变量同键会串 id）。
/// 主键未命中回落另一键域（i↔g 互通：[stacktop] self.X 的 TypeInst=0 实测指向全局符号，
/// 靠回落命中 "g:"——S2 黄金 buffer 的 pop.v.v [stacktop]self.scr_unitRenderDrawSprite）；
/// <b>"l:" 不回落</b>——回落会静默命中 Self 同名符号 = 错 id，宁拒（fail-closed）。
/// 函数：先注册表（内置，原始索引）后 NodeIndex（脚本，100000+codeId）——名字空间不重叠（S2 ②）。</summary>
public sealed class Translator : IOperandResolver
{
    readonly OpMsg op;
    readonly IReadOnlyDictionary<string, int> calibrated;
    readonly Func<string, int> registryIndexOf;
    readonly Func<string, int?> scriptCodeId;

    /// <summary>生产构造：全部留 null（从 AgentState/VarCalibrator/Registry/NodeIndex 静态取）。
    /// 测试经可选参数注入 fake。</summary>
    public Translator(OpMsg op,
        IReadOnlyDictionary<string, int>? calibrated = null,
        Func<string, int>? registryIndexOf = null,
        Func<string, int?>? scriptCodeId = null)
    {
        this.op = op;
        this.calibrated = calibrated ?? VarCalibrator.Map;
        this.registryIndexOf = registryIndexOf ?? Registry.IndexOf;
        this.scriptCodeId = scriptCodeId ?? (name => NodeIndex.TryGet(name, out var n) ? (int?)n.CodeId : null);
    }

    /// <summary>VarsMsg 键域选择（与 VarIdSimulator 的 "i:"/"g:"/"l:" 前缀规则同源）。</summary>
    internal static string KeyFor(short inst, string name) =>
        (inst == -5 ? "g:" : inst == -7 ? "l:" : "i:") + name;

    public uint ResolveVar(string name, short instType)
    {
        // 内置变量：exe 固定表 raw smallId（无 100000 偏置——S2 黄金实测
        // argument0=0x5D / sprite_index=0x1A / object_index=0x0E / image_* 全族）
        if (BuiltinVars.TryGetId(name, out int small)) return (uint)small;

        string key = KeyFor(instType, name);
        // "l:" 精确匹配不回落（845 实证碰撞：回落命中 Self 同名符号 = 静默错 id）；
        // i↔g 互回落保留（[stacktop]self.X 实证需要）
        string? alt = key[0] == 'l' ? null : key[0] == 'i' ? "g:" + name : "i:" + name;
        if (calibrated.TryGetValue(key, out int id) || (alt != null && calibrated.TryGetValue(alt, out id)))
            return (uint)(100000 + id);
        throw new TranslationRejectException(
            $"var '{name}' (inst {instType}) unresolved: not builtin, not calibrated (corpus miss or new variable)");
    }

    public int ResolveCall(string fn)
    {
        int idx = registryIndexOf(fn);
        if (idx >= 0) return idx;   // 内置/原生函数：注册表原始索引，无偏置（Task 11 新发现 3）
        int? codeId = scriptCodeId(fn);
        if (codeId != null) return 100000 + codeId.Value;   // gml_Script_*：100000+codeId（S2 ② 四重验证）
        throw new TranslationRejectException($"function '{fn}' unknown: not in registry, not in node index");
    }

    public int ResolveString(StrRef s)
    {
        var r = op.Strings.FirstOrDefault(x => x.Content == s.Content)
            ?? throw new TranslationRejectException($"string not in op table: '{s.Content}'");
        if (r.StrgIndex < 0)
            throw new TranslationRejectException($"non-boot string: '{s.Content}'");
        return r.StrgIndex;   // 运行时 stringId == boot STRG 索引（S2 ③ 1:1）
    }

    public int ResolveAsset(AssetRef a)
    {
        var r = op.Assets.FirstOrDefault(x => x.Kind == a.Kind && x.Index == a.Index)
            ?? throw new TranslationRejectException($"asset {a.Kind}[{a.Index}] not in op asset table");
        if (r.RuntimeIndex < 0)
            throw new TranslationRejectException($"asset {a.Kind}[{a.Index}] unresolved (RuntimeIndex=-1)");
        if (r.RuntimeIndex > int.MaxValue)
            throw new TranslationRejectException($"asset {a.Kind}[{a.Index}] runtime index {r.RuntimeIndex} out of range");
        return (int)r.RuntimeIndex;
    }
}
