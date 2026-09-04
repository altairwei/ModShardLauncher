using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>IOperandResolver 的生产实现（S2 ② 规则表 + Task 11 统一编码模型）。
/// 每 op 构造一次（携带该 op 的 Strings/Assets 上下文）。
///
/// 变量 id 解析顺序：活体校准表（VarCalibrator——proof 阶段与 apply 前 CalibOps 语料从活
/// buffer 逐条读回的真实值）→ 借位（仅 "l:"，#30）→ Reject。<b>#21 起模拟表兜底删除</b>
/// （_ally_hp 事故：Task 11 已证模拟有静态不可解残余偏移，00:05 真机 20 个未校准变量全走
/// 兜底 → Magic_Power 模拟 2269 写成活体 102269=_ally_hp。「兜底+记日志」= 用户看不见的
/// 静默错值；此类路径必须 fail-closed——VarIdSimulator 仅留作 VarCalibrator 的 drift 诊断；
/// 其值在 #30 起仅作借位候选池的一员，不再直接进操作数）。
/// 内置变量（exe 固定 smallId 表）优先于符号表——名字判据与 runner 语义同源（Task 11 Step 4/5）。
///
/// 键域与 VarIdSimulator 同源：指令 TypeInst == -5(Global) → "g:"，-7(Local) → "l:"，
/// 其余 → "i:"。局部独立成域（fix-loop #16 [V] 实证：vanilla 845 个 Local+非 Local VARI
/// 并存——key/state/i/j/target[Self,Global,Local]…，-7 落 "i:" 与实例变量同键会串 id）。
/// 主键未命中回落另一键域（i↔g 互通：[stacktop] self.X 的 TypeInst=0 实测指向全局符号，
/// 靠回落命中 "g:"——S2 黄金 buffer 的 pop.v.v [stacktop]self.scr_unitRenderDrawSprite）；
/// <b>"l:" 不回落</b>——回落会静默命中 Self 同名符号（借位语义下也无此路径，见
/// TryBorrowLocal）。
/// 函数：先注册表（内置，原始索引）→ 脚本活体校准（CallCalibrator——E2E seed 首证操作数 =
/// 100000+脚本表序，真机与 CODE 索引重合是巧合）→ NodeIndex 兜底（脚本，100000+node+0x88）
/// ——名字空间不重叠（S2 ②）。</summary>
public sealed class Translator : IOperandResolver
{
    readonly OpMsg op;
    readonly IReadOnlyDictionary<string, int> calibrated;
    readonly IReadOnlyDictionary<string, int>? simulated;
    readonly Func<string, int> registryIndexOf;
    readonly Func<string, int?> scriptCodeId;
    readonly IReadOnlyDictionary<string, int> callCalibrated;

    // #30 借位 id：l: miss 的确定性分配状态（首个 miss 时预扫全 op 建排除集）
    readonly Dictionary<string, int> borrowed = new();   // 新局部名 → 借位 id（同 op 同名同 id）
    HashSet<int>? usedInOp;                              // 本 op 已占用 id（校准命中 + 已借出）

    /// <summary>本 op 经借位分配的局部名 → id 明细（ApplyEngine 填进回执，MSL 侧可见）。</summary>
    public IReadOnlyDictionary<string, int> BorrowedLocals => borrowed;

    /// <summary>生产构造：全部留 null（从 AgentState/VarCalibrator/Registry/NodeIndex 静态取）。
    /// 测试经可选参数注入 fake。</summary>
    public Translator(OpMsg op,
        IReadOnlyDictionary<string, int>? calibrated = null,
        Func<string, int>? registryIndexOf = null,
        Func<string, int?>? scriptCodeId = null,
        IReadOnlyDictionary<string, int>? simulated = null,
        IReadOnlyDictionary<string, int>? callCalibrated = null)
    {
        this.op = op;
        this.calibrated = calibrated ?? VarCalibrator.Map;
        this.registryIndexOf = registryIndexOf ?? Registry.IndexOf;
        this.scriptCodeId = scriptCodeId ?? (name => NodeIndex.TryGet(name, out var n) ? (int?)n.CodeId : null);
        this.simulated = simulated ?? AgentState.VarMap;
        this.callCalibrated = callCalibrated ?? CallCalibrator.Map;
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
        // #30 借位 id：l: miss 不再拒——局部容器每次调用新建（invoker 0x14028B5C0 尾段
        // ctor(node+0xA0)），局部 id 只是容器 map 的键，执行面不查全局符号表（findings
        // 2026-09-04 §一/§四）。当场分配一个「既有符号表范围内」的未用 id：范围内借位 =
        // 报错文案打印成别人的名字（纯外观），越界 = 错误格式化器 OOB——所以候选只取
        // 校准表/模拟表的既有值，绝不编造。i:/g: miss 仍拒（共享容器：借位 = 与既有
        // 变量同槽别名 = _ally_hp 级静默错值）。
        if (key[0] == 'l')
        {
            if (TryBorrowLocal(name, out int bid))
                return (uint)(100000 + bid);
            throw new TranslationRejectException(
                $"var '{name}' (inst {instType}) unresolved: not calibrated and no borrowable local id (pool empty)");
        }
        throw new TranslationRejectException(
            $"var '{name}' (inst {instType}) unresolved: not builtin, not calibrated (corpus miss or new variable)");
    }

    /// <summary>#30 借位分配：确定性 = 池内（校准值 ∪ 模拟值）最小未用值，与字典枚举序无关。
    /// 排除集必须预扫全 op（<see cref="ScanUsedIds"/>）——惰性分配会与后续校准命中撞 id
    /// （同容器同键 = 静默别名）。同 op 同名同 id（记忆化）。池空 → false（fail-closed，
    /// 绝不编造越界 id）。容器每调用新建 → 借位 id 无须跨推送稳定（findings §五）。</summary>
    bool TryBorrowLocal(string name, out int id)
    {
        if (borrowed.TryGetValue(name, out id)) return true;
        usedInOp ??= ScanUsedIds();
        int best = int.MaxValue;
        foreach (int c in calibrated.Values)
            if (c >= 0 && c < best && !usedInOp.Contains(c)) best = c;
        if (simulated != null)
            foreach (int c in simulated.Values)
                if (c >= 0 && c < best && !usedInOp.Contains(c)) best = c;
        if (best == int.MaxValue) return false;
        borrowed[name] = best;
        usedInOp.Add(best);
        id = best;
        AgentState.Log($"borrow: '{op.Entry}' local '{name}' -> symbol id {best} (in-range, unused in op)");
        return true;
    }

    /// <summary>预扫本 op 全部指令，收集「已占用 id」= 每个非内置变量引用经校准表（含
    /// i↔g 回落）解析到的 id。l: miss 不占 id（由借位分配，分配时自动避开）；i:/g: miss
    /// 不在此报错（编码走到该指令时照旧拒——本方法只管排撞，不管拒绝）。</summary>
    HashSet<int> ScanUsedIds()
    {
        var used = new HashSet<int>();
        foreach (var sem in op.Instructions)
        {
            if (sem.Var == null || BuiltinVars.TryGetId(sem.Var, out _)) continue;
            string k = KeyFor(sem.Inst, sem.Var);
            if (calibrated.TryGetValue(k, out int id)) { used.Add(id); continue; }
            string? alt = k[0] == 'i' ? "g:" + sem.Var : k[0] == 'g' ? "i:" + sem.Var : null;
            if (alt != null && calibrated.TryGetValue(alt, out id)) used.Add(id);
        }
        return used;
    }

    public int ResolveCall(string fn)
    {
        int idx = registryIndexOf(fn);
        if (idx >= 0) return idx;   // 内置/原生函数：注册表原始索引，无偏置（Task 11 新发现 3）
        // 脚本：活体校准优先——操作数 = 100000+脚本表序，≠ node+0x88 的 CODE 索引
        // （E2E seed 首证两空间分离；真机 CODE 序巧合重合。见 CallCalibrator）
        if (callCalibrated.TryGetValue(fn, out int sid))
            return 100000 + sid;
        int? codeId = scriptCodeId(fn);
        if (codeId != null) return 100000 + codeId.Value;   // 兜底：真机 CODE 序巧合下正确（无语料命中时不回归）
        throw new TranslationRejectException($"function '{fn}' unknown: not in registry, not calibrated, not in node index");
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
