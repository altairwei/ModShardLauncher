using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>IOperandResolver 的生产实现（S2 ② 规则表 + Task 11 统一编码模型）。
/// 每 op 构造一次（携带该 op 的 Strings/Assets 上下文）。
///
/// 变量 id 解析顺序：活体校准表（VarCalibrator——proof 阶段从活 buffer 逐条读回的真实值）
/// → VarsMsg 模拟表（Task 11 诚实结论：有静态不可解的残余偏移，仅作后备，用一次记一次日志）
/// → Reject（product-only 新变量，取舍清单）。
/// 内置变量（exe 固定 smallId 表）优先于符号表——名字判据与 runner 语义同源（Task 11 Step 4/5）。
///
/// 键域与 VarIdSimulator 同源：指令 TypeInst == -5(Global) → "g:"，其余 → "i:"；
/// 主键未命中回落另一键域（[stacktop] self.X 的 TypeInst=0 实测指向全局符号，
/// 靠回落命中 "g:"——S2 黄金 buffer 的 pop.v.v [stacktop]self.scr_unitRenderDrawSprite）。
/// 函数：先注册表（内置，原始索引）后 NodeIndex（脚本，100000+codeId）——名字空间不重叠（S2 ②）。</summary>
public sealed class Translator : IOperandResolver
{
    readonly OpMsg op;
    readonly IReadOnlyDictionary<string, int> vars;
    readonly IReadOnlyDictionary<string, int> calibrated;
    readonly Func<string, int> registryIndexOf;
    readonly Func<string, int?> scriptCodeId;
    readonly HashSet<string> simulatedLogged = new();

    /// <summary>生产构造：全部留 null（从 AgentState/VarCalibrator/Registry/NodeIndex 静态取）。
    /// 测试经可选参数注入 fake。</summary>
    public Translator(OpMsg op,
        IReadOnlyDictionary<string, int>? vars = null,
        IReadOnlyDictionary<string, int>? calibrated = null,
        Func<string, int>? registryIndexOf = null,
        Func<string, int?>? scriptCodeId = null)
    {
        this.op = op;
        this.vars = vars ?? AgentState.VarMap ?? new Dictionary<string, int>();
        this.calibrated = calibrated ?? VarCalibrator.Map;
        this.registryIndexOf = registryIndexOf ?? Registry.IndexOf;
        this.scriptCodeId = scriptCodeId ?? (name => NodeIndex.TryGet(name, out var n) ? (int?)n.CodeId : null);
    }

    /// <summary>VarsMsg 键域选择（与 VarIdSimulator 的 "i:"/"g:" 前缀规则同源）。</summary>
    internal static string KeyFor(short inst, string name) => (inst == -5 ? "g:" : "i:") + name;

    public uint ResolveVar(string name, short instType)
    {
        // 内置变量：exe 固定表 raw smallId（无 100000 偏置——S2 黄金实测
        // argument0=0x5D / sprite_index=0x1A / object_index=0x0E / image_* 全族）
        if (BuiltinVars.TryGetId(name, out int small)) return (uint)small;

        string key = KeyFor(instType, name);
        string alt = key[0] == 'i' ? "g:" + name : "i:" + name;
        if (calibrated.TryGetValue(key, out int id) || calibrated.TryGetValue(alt, out id))
            return (uint)(100000 + id);
        if (vars.TryGetValue(key, out id) || vars.TryGetValue(alt, out id))
        {
            // 模拟值兜底：Task 11 已证有残余偏移——用可以，但必须留痕
            if (simulatedLogged.Add(key))
                AgentState.Log($"var '{key}' resolved via simulated id {id} (uncalibrated)");
            return (uint)(100000 + id);
        }
        throw new TranslationRejectException($"var '{name}' (inst {instType}) unmapped: not builtin, not calibrated, not in VarsMsg");
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
