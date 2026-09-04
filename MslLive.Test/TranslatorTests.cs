using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>Translator 规则表逐行覆盖（合成指令 + fake 解析源），含全部 Reject 路径。
/// 编码断言直接对字节（编码器与翻译器的合同面）：
/// 指令字 = opcode&lt;&lt;24 | T2&lt;&lt;20 | T1&lt;&lt;16 | low16；变量操作数 = RefTop&lt;&lt;24 | low24。</summary>
public class TranslatorTests
{
    static readonly Dictionary<string, int> Calibrated = new()
        { ["i:drifty"] = 777, ["g:gsave"] = 2000 };
    static readonly Dictionary<string, int> RegistryFake = new() { ["sprite_exists"] = 645 };
    static readonly Dictionary<string, int> Scripts = new() { ["gml_Script_foo"] = 1350 };

    static Translator Make(OpMsg? op = null) => new(op ?? new OpMsg(),
        calibrated: Calibrated,
        registryIndexOf: n => RegistryFake.GetValueOrDefault(n, -1),
        scriptCodeId: n => Scripts.TryGetValue(n, out var v) ? v : null);

    static byte[] EncodeOne(SemInstruction sem, Translator t) => BcEncoder.Encode(new List<SemInstruction> { sem }, t);

    static SemInstruction PushVar(string name, short inst = -1, byte refTop = 0xA0) =>
        new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable, Inst = inst, Var = name, RefTop = refTop };

    [Fact]
    public void Var_Builtin_RawSmallId_NoBias()
    {
        // sprite_index=26（exe 表锚点）：0xA0<<24 | 0x1A，无 100000 偏置（Task 11 未决项的黄金回答）
        byte[] b = EncodeOne(PushVar("sprite_index"), Make());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x05, 0xC0, 0x1A, 0x00, 0x00, 0xA0 }, b);
    }

    [Fact]
    public void Var_Calibrated_InstanceScope()
    {
        // 校准值 777 → 100777 = 0x189A9（校准语料收割是变量 id 的唯一真源——#21）
        byte[] b = EncodeOne(PushVar("drifty"), Make());
        Assert.Equal(0xA00189A9u, BitConverter.ToUInt32(b, 4));
    }

    [Fact]
    public void Var_UncalibratedSimulated_Reject()
    {
        // #21 fail-closed：模拟表兜底已删除（_ally_hp 事故——模拟 i:2269 写成活体 102269，
        // 活体 2269 号位实为 _ally_hp；Task 11 早已证模拟有静态不可解漂移）。未校准即拒，
        // 宁可整批不推也不静默错装。spr 此刻不在校准表 → 必拒。
        var ex = Assert.Throws<TranslationRejectException>(() => EncodeOne(PushVar("spr"), Make()));
        Assert.Contains("not calibrated", ex.Message);
    }

    [Fact]
    public void Var_GlobalScope_GKeysFirst()
    {
        // TypeInst=-5 → 先查 "g:"：gsave 校准值 2000 → 102000 = 0x18E70
        byte[] b = EncodeOne(PushVar("gsave", inst: -5), Make());
        Assert.Equal(0xA0018E70u, BitConverter.ToUInt32(b, 4));
    }

    [Fact]
    public void Var_StacktopForm_KeepsRefTop80_AndFallsBackToGKey()
    {
        // [stacktop]self.X：TypeInst=0 → 主键 "i:" 未命中 → 回落 "g:"；RefTop 0x80 原样保留
        // （S2 黄金 pop.v.v [stacktop]self.scr_unitRenderDrawSprite = 0x80|(100000+1217)）
        var calibrated = new Dictionary<string, int> { ["g:scr_unitRenderDrawSprite"] = 1217 };
        var t = new Translator(new OpMsg(), calibrated: calibrated,
            registryIndexOf: _ => -1, scriptCodeId: _ => null);
        var sem = new SemInstruction
        { Kind = BcEncoder.OpPop, T1 = BcEncoder.TVariable, T2 = BcEncoder.TVariable, Inst = 0, Var = "scr_unitRenderDrawSprite", RefTop = 0x80 };
        byte[] b = EncodeOne(sem, t);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x55, 0x45, 0x61, 0x8B, 0x01, 0x80 }, b);
    }

    [Fact]
    public void Var_LocalScope_UsesLKey()
    {
        // TypeInst=-7 → "l:" 键域（[V] 实证：vanilla 845 个 Local+非 Local VARI 并存，
        // 如 target[Self,Global,Local]——分域才不串 id。旧代码落 "i:" 与实例变量同键）
        var calibrated = new Dictionary<string, int> { ["l:key"] = 55, ["i:key"] = 999 };
        var t = new Translator(new OpMsg(), calibrated: calibrated,
            registryIndexOf: _ => -1, scriptCodeId: _ => null);
        byte[] b = EncodeOne(PushVar("key", inst: -7), t);
        Assert.Equal(0xA00186D7u, BitConverter.ToUInt32(b, 4));   // 100000+55 = 100055，不是 i: 的 999
    }

    // ---- #30 借位 id（l: miss → 既有符号表范围内最小未用 id；findings 2026-09-04 §四/§五：
    // 局部容器每次调用新建，局部 id 只是容器 map 的键，执行面不查全局符号表——新局部名
    // 可当场借一个范围内 id，无需运行时原语。旧 Var_LocalScope_Miss_DoesNotFallBackToI_Reject
    // 钉的「l: miss 必拒」被 #30 有意取代）----

    /// <summary>借位用例构造：op 必须携带指令流（借位预扫读 op.Instructions 全量）；
    /// simulated 显式注入（默认空字典钉死池——不读 ambient AgentState.VarMap，测试不随
    /// 执行顺序漂移）。</summary>
    static (Translator t, byte[] Encoded) EncodeBorrow(
        List<SemInstruction> insts, Dictionary<string, int> calibrated,
        Dictionary<string, int>? simulated = null)
    {
        var op = new OpMsg { Instructions = insts };
        var t = new Translator(op, calibrated: calibrated,
            registryIndexOf: _ => -1, scriptCodeId: _ => null,
            simulated: simulated ?? new Dictionary<string, int>());
        return (t, BcEncoder.Encode(insts, t));
    }

    /// <summary>#30：l: miss 借「池内最小未用值」——确定性（与字典枚举序无关，取数值最小）。
    /// 池内 5 未被本 op 引用 → 借 5，不是 9。</summary>
    [Fact]
    public void Var_LocalMiss_Borrows_MinUnusedId_Deterministic()
    {
        var calibrated = new Dictionary<string, int> { ["l:known"] = 5, ["i:other"] = 9 };
        var (_, b) = EncodeBorrow(new List<SemInstruction> { PushVar("fresh", inst: -7) }, calibrated);
        Assert.Equal(0xA00186A5u, BitConverter.ToUInt32(b, 4));   // 100000+5 = 100005
    }

    /// <summary>#30 预扫排撞：借位必须排除本 op 已引用的全部校准 id——惰性分配会与后续
    /// 校准命中撞 id（同容器同键 = 静默别名）。池 {5, 9}，l:known 已占 5 → fresh 只能借 9。</summary>
    [Fact]
    public void Var_LocalBorrow_ExcludesIdsReferencedInSameOp()
    {
        var calibrated = new Dictionary<string, int> { ["l:known"] = 5, ["i:other"] = 9 };
        var insts = new List<SemInstruction> { PushVar("known", inst: -7), PushVar("fresh", inst: -7) };
        var (_, b) = EncodeBorrow(insts, calibrated);
        Assert.Equal(0xA00186A5u, BitConverter.ToUInt32(b, 4));    // known = 校准 5（非借位）
        Assert.Equal(0xA00186A9u, BitConverter.ToUInt32(b, 12));   // fresh 借 9（100009 = 0x186A9）
    }

    /// <summary>#30：同 op 同名同 id（记忆化）+ 不同新名不撞（顺序分配最小未用）；
    /// BorrowedLocals 明细完整（回执据此带分配明细）。</summary>
    [Fact]
    public void Var_LocalBorrow_SameNameSameId_DistinctNamesDistinct()
    {
        var calibrated = new Dictionary<string, int> { ["i:x"] = 5, ["i:y"] = 9 };
        var insts = new List<SemInstruction>
        {
            PushVar("fresh_a", inst: -7), PushVar("fresh_b", inst: -7), PushVar("fresh_a", inst: -7),
        };
        var (t, b) = EncodeBorrow(insts, calibrated);
        Assert.Equal(0xA00186A5u, BitConverter.ToUInt32(b, 4));    // fresh_a 借 5
        Assert.Equal(0xA00186A9u, BitConverter.ToUInt32(b, 12));   // fresh_b 借 9（100009，不与 a 撞）
        Assert.Equal(0xA00186A5u, BitConverter.ToUInt32(b, 20));   // fresh_a 记忆化 = 5
        Assert.Equal(2, t.BorrowedLocals.Count);
        Assert.Equal(5, t.BorrowedLocals["fresh_a"]);
        Assert.Equal(9, t.BorrowedLocals["fresh_b"]);
    }

    /// <summary>#30（承接 fix #16 的 845 碰撞论证）：借位不回落 i:——l:key miss 且
    /// i:key=999 在池内时，若走「i: 回落」会得 999；借位语义取池内最小未用（=3），
    /// 数值上可区分。i↔g 回落保留不变（[stacktop]self.X 实证需要，见上）。</summary>
    [Fact]
    public void Var_LocalBorrow_DoesNotFallBackToI()
    {
        var calibrated = new Dictionary<string, int> { ["i:key"] = 999, ["l:zzz"] = 5, ["i:aaa"] = 3 };
        var insts = new List<SemInstruction> { PushVar("zzz", inst: -7), PushVar("key", inst: -7) };
        var (_, b) = EncodeBorrow(insts, calibrated);
        Assert.Equal(0xA00186A5u, BitConverter.ToUInt32(b, 4));    // zzz = 校准 5
        Assert.Equal(0xA00186A3u, BitConverter.ToUInt32(b, 12));   // key 借 3（100003），不是 i: 的 999
    }

    /// <summary>#30：池并入模拟表（VarsMsg）——校准空、模拟有值时仍可借。借位 id 只需
    /// 「boot 符号表范围内 + 本 op 内唯一」，不是真名解析，模拟表的 drift 无害。</summary>
    [Fact]
    public void Var_LocalBorrow_PoolIncludesSimulatedMap()
    {
        var (t, b) = EncodeBorrow(new List<SemInstruction> { PushVar("fresh", inst: -7) },
            new Dictionary<string, int>(), simulated: new Dictionary<string, int> { ["i:anything"] = 42 });
        Assert.Equal(0xA00186CAu, BitConverter.ToUInt32(b, 4));    // 100000+42 = 100042
        Assert.Equal(42, t.BorrowedLocals["fresh"]);
    }

    /// <summary>#30 fail-closed：池空（校准/模拟皆无值）→ 拒，绝不编造 id——越界 id 会打穿
    /// 错误格式化器的符号表反查（findings §四）。</summary>
    [Fact]
    public void Var_LocalBorrow_EmptyPool_FailClosed()
    {
        var insts = new List<SemInstruction> { PushVar("fresh", inst: -7) };
        var op = new OpMsg { Instructions = insts };
        var t = new Translator(op, calibrated: new Dictionary<string, int>(),
            registryIndexOf: _ => -1, scriptCodeId: _ => null, simulated: new Dictionary<string, int>());
        var ex = Assert.Throws<TranslationRejectException>(() => BcEncoder.Encode(insts, t));
        Assert.Contains("borrow", ex.Message);
    }

    [Fact]
    public void Var_Unmapped_Reject()
    {
        var ex = Assert.Throws<TranslationRejectException>(() => EncodeOne(PushVar("product_only_new_var"), Make()));
        Assert.Contains("not calibrated", ex.Message);
    }

    [Fact]
    public void Call_Builtin_RegistryRawIndex()
    {
        var sem = new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = 1, Fn = "sprite_exists" };
        byte[] b = EncodeOne(sem, Make());
        Assert.Equal(new byte[] { 0x01, 0x00, 0x02, 0xD9, 0x85, 0x02, 0x00, 0x00 }, b);   // 645
    }

    [Fact]
    public void Call_Script_100000PlusCodeId()
    {
        var sem = new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = 0, Fn = "gml_Script_foo" };
        byte[] b = EncodeOne(sem, Make());
        Assert.Equal(101350u, BitConverter.ToUInt32(b, 4));   // 100000+1350
    }

    [Fact]
    public void Call_RegistryWinsOverScript()   // 名字空间不重叠的防御性固定（S2 ②）
    {
        RegistryFake["gml_Script_foo"] = 42;
        try
        {
            var t = Make();
            Assert.Equal(42, t.ResolveCall("gml_Script_foo"));
        }
        finally { RegistryFake.Remove("gml_Script_foo"); }
    }

    [Fact]
    public void Call_Unknown_Reject()
    {
        var sem = new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = 1, Fn = "no_such_fn" };
        Assert.Throws<TranslationRejectException>(() => EncodeOne(sem, Make()));
    }

    [Fact]
    public void String_BootStrgIndex()
    {
        var op = new OpMsg { Strings = { new StrRef { Content = "Unit ", StrgIndex = 2480 } } };
        var sem = new SemInstruction { Kind = BcEncoder.OpPush, T1 = BcEncoder.TString, Str = "Unit " };
        byte[] b = EncodeOne(sem, Make(op));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x06, 0xC0, 0xB0, 0x09, 0x00, 0x00 }, b);
    }

    [Fact]
    public void String_NonBoot_Reject()
    {
        var op = new OpMsg { Strings = { new StrRef { Content = "fresh", StrgIndex = -1 } } };
        var sem = new SemInstruction { Kind = BcEncoder.OpPush, T1 = BcEncoder.TString, Str = "fresh" };
        var ex = Assert.Throws<TranslationRejectException>(() => EncodeOne(sem, Make(op)));
        Assert.Contains("non-boot string", ex.Message);
    }

    [Fact]
    public void Asset_PushI_RuntimeIndex_InLow16()
    {
        var op = new OpMsg { Assets = { new AssetRef { Kind = "Sprite", Index = 17248, RuntimeIndex = 18000 } } };
        var sem = new SemInstruction
        { Kind = BcEncoder.OpPushI, T1 = BcEncoder.TInt16, Int = 17248, AssetKinds = new List<string> { "Sprite" } };
        byte[] b = EncodeOne(sem, Make(op));
        Assert.Equal(new byte[] { 0x50, 0x46, 0x0F, 0x84 }, b);   // 18000 = 0x4650
    }

    [Fact]
    public void Asset_Unresolved_Reject()
    {
        var op = new OpMsg { Assets = { new AssetRef { Kind = "Sprite", Index = 17248, RuntimeIndex = -1 } } };
        var sem = new SemInstruction
        { Kind = BcEncoder.OpPush, T1 = BcEncoder.TInt32, Int = 17248, AssetKinds = new List<string> { "Sprite" } };
        Assert.Throws<TranslationRejectException>(() => EncodeOne(sem, Make(op)));
    }

    [Fact]
    public void Literal_AndBranch_Verbatim()
    {
        // pushi.e -9 + b +166：字面量/分支逐字复制（S2 ② 首行规则）
        var insts = new List<SemInstruction>
        {
            new() { Kind = BcEncoder.OpPushI, T1 = BcEncoder.TInt16, Low16 = unchecked((ushort)(short)-9), Int = -9 },
            new() { Kind = BcEncoder.OpB, Jump = 166 },
        };
        byte[] b = BcEncoder.Encode(insts, Make());
        Assert.Equal(new byte[] { 0xF7, 0xFF, 0x0F, 0x84, 0xA6, 0x00, 0x00, 0xB6 }, b);
    }

    [Fact]
    public void Goto_Negative_Uses23BitFileForm()
    {
        // popenv -159（S2 黄金实测 61 FF 7F BB）：内存语义 24 位负数 → 文件 23 位负数形态
        var sem = new SemInstruction { Kind = BcEncoder.OpPopEnv, Jump = -159 };
        byte[] b = BcEncoder.Encode(new List<SemInstruction> { sem }, Make());
        Assert.Equal(new byte[] { 0x61, 0xFF, 0x7F, 0xBB }, b);
    }

    [Fact]
    public void Goto_PopenvExitMagic_Passthrough()
    {
        var sem = new SemInstruction { Kind = BcEncoder.OpPopEnv, Jump = 0xF00000 };
        byte[] b = BcEncoder.Encode(new List<SemInstruction> { sem }, Make());
        Assert.Equal(new byte[] { 0x00, 0x00, 0xF0, 0xBB }, b);
    }

    [Fact]
    public void Double_AndLong_Push_8ByteOperand()
    {
        var insts = new List<SemInstruction>
        {
            new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TDouble, Real = 1.5 },
            new() { Kind = BcEncoder.OpPush, T1 = BcEncoder.TInt64, Int = 0x1_0000_0001 },
        };
        byte[] b = BcEncoder.Encode(insts, Make());
        Assert.Equal(4 + 8 + 4 + 8, b.Length);
        Assert.Equal(1.5, BitConverter.ToDouble(b, 4));
        Assert.Equal(0x1_0000_0001, BitConverter.ToInt64(b, 12 + 4));
    }
}
