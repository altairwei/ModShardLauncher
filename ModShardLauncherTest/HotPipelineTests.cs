using System.IO.Pipes;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using MslLive.Shared;
using UndertaleModLib;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class HotPipelineTests : IDisposable
{
    readonly UndertaleData? savedData;
    readonly bool savedDev;
    readonly string? savedPath;

    public HotPipelineTests()
    {
        savedData = DataLoader.data;
        savedDev = Main.Settings.DevMode;
        savedPath = DataLoader.savedDataPath;
    }
    public void Dispose()
    {
        if (savedData != null) DataLoader.data = savedData;
        Main.Settings.DevMode = savedDev;
        DataLoader.savedDataPath = savedPath;
        LiveSession.ForRunningGameOverride = null;
        LiveSession.Current?.End();
        BaselineStore.Reset();
    }

    static UndertaleData Load()
    {
        UndertaleData data;
        using (var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });
        // LiveStubInjector 的 ReferenceEquals 守卫 + Msl.* 原语都硬绑定 ModLoader.Data——
        // 每次装载后指到最新这份（boot 注入完再装 product，顺序上各自注入时各自是 ModLoader.Data）
        DataLoader.data = data;
        return data;
    }

    static SessionState NewAlloc(UndertaleData boot)
    {
        var shells = boot.GameObjects.Select((o, i) => (o, i))
            .Where(t => t.o.Name.Content.StartsWith("o_msl_shell_"))
            .Select(t => (t.i, t.o.ParentId?.Name?.Content ?? "")).ToList();
        var s = new SessionState(new LiveQuotas(), shells);
        s.SetBlanks(new BlanksMsg { SpriteFirst = boot.Sprites.Count, SpriteCount = 64, PathFirst = boot.Paths.Count, PathCount = 16 });
        return s;
    }

    [Fact]
    public void NoChanges_EmptyBatch()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        Assert.Empty(r.Batch!.Ops);
        Assert.Empty(r.Batch.CalibOps);   // #21：无 op 即无需求键，语料为空
    }

    [Fact]
    public void ChangedEntry_ProducesSwapOp()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var target = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        // #21：变量须为 baseline 已知名（msl_probe_step 在 stub 里）——全新名字会被诚实拒批
        target.ReplaceGML("msl_live_apply();\nglobal.msl_probe_step = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        var op = Assert.Single(r.Batch!.Ops, o => o.Entry == "gml_Object_o_msl_live_Step_0");
        Assert.Equal("swap", op.Kind);
        Assert.Contains("msl_probe_step", op.Variables);
        // #21：语料覆盖——g:msl_probe_step 的来源 = baseline 的 stub 自身
        Assert.Contains(r.Batch.CalibOps, c => c.Entry == "gml_Object_o_msl_live_Step_0"
            && c.Variables.Contains("msl_probe_step"));
    }

    /// <summary>#21 fail-closed 历史锚点（演化链：#21 新变量名全拒 → #30 l: 放行 → #36-B
    /// i: 救援 → #37 g: 救援）。#37（用户批准）起，编辑引入 baseline 从未见过的全局变量名
    /// 改写为 variable_global_set(名字, 值)——不再拒批。本用例载荷为混合形态（loader 调用 +
    /// 新全局写）：改写不得扰动 msl_live_apply() 调用，新全局名不得残留 g: 键引用。
    /// 剩余拒批面 = 槽 entry（见 <see cref="NewScriptSlot_NewGlobalVar_Rejected_HonestBoundary"/>）
    /// 与数组壳/字符串字面量守卫跳过后的自然拒批。</summary>
    [Fact]
    public void ChangedEntry_NewGlobalVar_RescuedByDynamicApi()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var target = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        target.ReplaceGML("msl_live_apply();\nglobal.msl_never_seen_xyz = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Entry == "gml_Object_o_msl_live_Step_0");
        // 新全局名不再以 g: 键引用（CalibCorpus 拒批面解除）
        Assert.DoesNotContain(op.Instructions, i => i.Var == "msl_never_seen_xyz");
        Assert.DoesNotContain("msl_never_seen_xyz", op.Variables);
        // 改写形态：variable_global_set(名字, 值) argc=2，名字落字符串表
        Assert.Contains(op.Instructions, i => i.Fn == "variable_global_set" && i.Low16 == 2);
        Assert.Contains(op.Strings, s => s.Content == "msl_never_seen_xyz");
    }

    /// <summary>#30：新增局部变量名（boot baseline 无来源）不再拒批——l: miss 由 agent
    /// 借位分配 id（局部容器每次调用新建，局部 id 只是容器 map 键，执行面不查全局符号表
    /// ——findings 2026-09-04 §四/§五）。旧「新变量名无法热分配 id」拒批正是 #30 立项根因
    /// （scr_console_help 加 4 局部被拒）。载荷局部数 2 &gt; boot 帧容量 1 也随 Gate 2 重塑
    /// 放行（容量上限删除——agent 侧语义，见 MslLive.Test ApplyEngineTests）。</summary>
    [Fact]
    public void ChangedEntry_NewLocalName_NotRejected()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var target = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        target.ReplaceGML("msl_live_apply();\nvar _brand_new_local_xyz;\n_brand_new_local_xyz = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Entry == "gml_Object_o_msl_live_Step_0");
        // 载荷确实引用了新局部名（l: 键域——agent 侧将借位）
        Assert.Contains(op.Instructions, i => i.Var == "_brand_new_local_xyz" && i.Inst == -7);
    }

    /// <summary>#30-D 槽形（新脚本 wrapper，probe6 路径）：新增局部名经救援重写为局部域。
    /// 旧编译器把 var 局部编译成 Self 域（"i:" 键——probe6 实证），槽 wrapper 无 CodeLocals
    /// → 判别子 = product Local-VARI − boot Local-VARI 差集（旧编译器对 var 局部恒注册
    /// Local-VARI，纯语句/函数形皆然）。函数形子条目的编译 LocalsCount=0 是谎——救援须连带
    /// 兜底 ≥1，否则 -7 引用 + 帧 0 局部 = 激活门关死 = 局部访问静默跳过（probe3/6 实证）。</summary>
    [Fact]
    public void NewScriptSlot_NewLocal_RescuedToLocalDomain()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        Msl.AddFunction("function scr_slot_local() { var _slot_fresh_local;\n_slot_fresh_local = 7;\nreturn _slot_fresh_local; }", "scr_slot_local");
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_slot_local();", product);
        var alloc = NewAlloc(boot);
        var r = HotPipeline.BuildBatch(boot, product, alloc,
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var slotOp = r.Batch!.Ops.First(o => o.Entry == "msl_slot_0");
        // 载荷确实把新局部名放进了局部域（-7）——agent 侧将借位 id
        Assert.Contains(slotOp.Instructions, i => i.Var == "_slot_fresh_local" && i.Inst == -7);
        // 谎 LocalsCount=0 兜底（函数形子条目编译值 0 是谎，probe6；激活门要求 ≥1）
        Assert.Equal(1, slotOp.LocalsCount);
    }

    /// <summary>#30-D vanilla 编辑（既有未测洞，probe6 Q11a 路径）：boot 侧 = 真 GMS 编译
    /// （局部 = l:/-7 域），product 侧 ReplaceGML = 旧编译器（局部 = i:/-1 域）→ 老局部名
    /// i: 键全 miss → 整批拒。vanilla 条目预存 CodeLocals 且 ReplaceGML 更新它（probe6
    /// Q11a/Q11b，函数体局部也收）→ 判别子 = CodeLocals。救援后老名变 l: 从 boot 校准
    /// （不再借位——boot 有真 id）。</summary>
    [Fact]
    public void VanillaEdit_VarLocal_RescuedAndCalibratedFromBootLKey()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        // 挑一个带 var 局部的 vanilla 对象事件（根条目、真 GMS 编译 = l: 引用）；
        // 挑名保持确定性（同数据每次同一 entry）
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_")
            && c.Instructions.Any(i => (short)i.TypeInst == -7 && VarRefName(i) is string n && n.StartsWith("_")));
        string? found = null;
        foreach (var i in bootEntry.Instructions)
            if ((short)i.TypeInst == -7 && VarRefName(i) is string n && n.StartsWith("_")) { found = n; break; }
        Assert.NotNull(found);
        string localName = found!;
        // product 侧同一事件用旧编译器重编（var 局部 = i: 键）——用户改 vanilla 事件的真实形态
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML($"var {localName};\n{localName} = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops);
        Assert.Equal(bootEntry.Name.Content, op.Entry);
        // 救援：载荷局部引用从实例域（-1）改写为局部域（-7）
        Assert.Contains(op.Instructions, i => i.Var == localName && i.Inst == -7);
        Assert.DoesNotContain(op.Instructions, i => i.Var == localName && i.Inst == -1);
        // 老名不再进借位名单——boot 侧 l: 引用成为校准语料来源（真 id，非借位）
        Assert.Contains(r.Batch.CalibOps, c => c.Variables.Contains(localName));
    }

    static string? VarRefName(UndertaleInstruction i) =>
        (i.Value as UndertaleInstruction.Reference<UndertaleVariable>)?.Target?.Name.Content
        ?? i.Destination?.Target?.Name.Content;

    /// <summary>#30-D 数组形态（fix #32：09-04 21:58 实弹形态）：旧编译器对函数声明体内的
    /// 数组元素访问（push.v.d 读 / pop.v.v 存，VARI Target=Self 条目）发射 TypeInst=
    /// Undefined(0)；同函数的普通 var 引用则是正确的 Local(-7)/PushLoc（21:58 产物 dump
    /// 实证：_rows/_desc/_villageRep 的数组指令全 0 形态、普通指令全 -7——probe6 的 -1
    /// 形态只在顶层语句上下文出现）。0 → KeyFor "i:" → 无 boot 来源 → 救援翻转
    /// sem.Inst==-1 匹配不上 0 → flipped=0 → 整批拒（scr_console_help 热推被拒实弹）。
    /// 救援须连 0 形态一起翻 -7。</summary>
    [Fact]
    public void NewScriptSlot_ArrayLocal_UndefinedDomain_RescuedToLocalDomain()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        Msl.AddFunction("function scr_slot_arr() { var _slot_arr;\n_slot_arr = [1, 2, 3];\nreturn _slot_arr[1]; }", "scr_slot_arr");
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_slot_arr();", product);
        var alloc = NewAlloc(boot);
        var r = HotPipeline.BuildBatch(boot, product, alloc,
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var slotOp = r.Batch!.Ops.First(o => o.Entry == "msl_slot_0");
        // 数组元素访问的 0 形态载荷一并翻到局部域（-7）——翻完不得残留 0 域引用
        Assert.Contains(slotOp.Instructions, i => i.Var == "_slot_arr" && i.Inst == -7);
        Assert.DoesNotContain(slotOp.Instructions, i => i.Var == "_slot_arr" && i.Inst == 0);
    }

    /// <summary>#30-D 数组形态·vanilla 面（09-04 21:58 实弹 = scr_console_help/_rows 同款）：
    /// vanilla 条目重编 + 数组局部 → 判别子走 CodeLocals（ReplaceGML 更新，函数体局部也收）
    /// → 数组元素 0 形态载荷同样须翻 -7，不得因域形态整批拒。</summary>
    [Fact]
    public void VanillaEdit_ArrayLocal_UndefinedDomain_RescuedToLocalDomain()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        // 挑一个确定性 vanilla 根条目（对象事件面，同 VanillaEdit_VarLocal 测试）
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("var _van_arr_local;\n_van_arr_local = [1, 2, 3];\n_van_arr_local[0] = 9;\nreturn _van_arr_local[0];", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Kind == "swap");
        Assert.Equal(bootEntry.Name.Content, op.Entry);
        Assert.Contains(op.Instructions, i => i.Var == "_van_arr_local" && i.Inst == -7);
        Assert.DoesNotContain(op.Instructions, i => i.Var == "_van_arr_local" && i.Inst == 0);
    }

    /// <summary>fix #36-B（真机 16:06 实弹形态）：新实例变量（product Self-VARI − boot
    /// Self-VARI 差集判别——vari-probe 实证新实例变量 VARI 条目 = Self 域，与 var 局部的
    /// Local 域正交）的引用改写为官方动态 API——RE findings 2026-09-05 §二/§三：静态 id
    /// 走实例变量层的槽数组链（无写时扩容：读越层静默、写越层报错），动态 API 按名字走
    /// 符号 find-or-create + map 存储，对 boot 前创建的旧实例天然有效（A/C 方案的死角）。
    /// 读 = push.v self.id + push.s 名字 + call variable_instance_get(argc=2)；
    /// 写 = 借位局部中转 + push.v self.id + push.s 名字 + pushloc 中转 + call
    /// variable_instance_set(argc=3)。改写后载荷不再引用新实例变量名（CalibCorpus 自然
    /// 放行——16:06 的「共享容器无法安全借位」拒绝面解除）。</summary>
    /// <summary>fix #36-B 槽载荷边界：product-only 槽 entry（新脚本）不走源码层重写
    /// （ReplaceGML 对其子条目结构重排会 IndexOutOfRange——槽载荷是 AddFunction 建的
    /// wrapper，无 baseline 对应物，E2E NewScriptViaSlot 实证），引用新实例变量 →
    /// 原拒批路径放行（共享容器红线——重启游戏后由正常载入覆盖）。</summary>
    [Fact]
    public void NewScriptSlot_NewInstanceVar_Rejected_HonestBoundary()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        Msl.AddFunction("function scr_slot_ivar() { fresh_slot_ivar = 41;\nreturn fresh_slot_ivar + 1; }", "scr_slot_ivar");
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_slot_ivar();", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Null(r.Batch);   // 整批不推（fail-closed）
        Assert.Contains(r.Failures, f => f.Contains("fresh_slot_ivar") && f.Contains("boot baseline 无来源"));
    }

    /// <summary>fix #36-B vanilla 面（真机 16:06 的 o_devconsole 形态）：既有对象事件的
    /// 新实例变量（读 + 写 + 跨事件共享语义）同样改写；中转变量走 #30 借位局部。</summary>
    [Fact]
    public void VanillaEdit_NewInstanceVar_RewrittenToDynamicApi()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("fresh_van_ivar = 5;\nreturn fresh_van_ivar * 2;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Kind == "swap");
        Assert.DoesNotContain(op.Instructions, i => i.Var == "fresh_van_ivar");
        Assert.Contains(op.Instructions, i => i.Fn == "variable_instance_get" && i.Low16 == 2);
        Assert.Contains(op.Instructions, i => i.Fn == "variable_instance_set" && i.Low16 == 3);
        Assert.Contains(op.Strings, s => s.Content == "fresh_van_ivar");
    }

    /// <summary>fix #40（真机 00:21 实弹形态，o_devconsole_Create_0）：entry 无任何代码层
    /// 数组访问，仅因无关字符串字面量含 '['（"[DevTools] " 日志前缀）被 #36-B 守卫整 entry
    /// 连坐跳过 → 残留引用整批拒批（同批 Draw_64 同名变量的成功改写被连带作废）。'['] 检查
    /// 须查剥串文本——数组壳是代码层结构，字符串里的 '[' 与两轮改写正则无任何交互。
    /// 探针实证（真机产物反编译）：该 entry 恰 2 个 '[' 全在字符串内，剥串后 0。</summary>
    [Fact]
    public void VanillaEdit_FreshIVar_StringBracketLiteral_Rescued()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("fresh_str_ivar = 5;\nreturn string(fresh_str_ivar) + \"[DevTools] \";", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Kind == "swap");
        Assert.DoesNotContain(op.Instructions, i => i.Var == "fresh_str_ivar");
        Assert.Contains(op.Instructions, i => i.Fn == "variable_instance_set" && i.Low16 == 3);
        Assert.Contains(op.Instructions, i => i.Fn == "variable_instance_get" && i.Low16 == 2);
        // 无关字符串字面量原样保留（含 '['）
        Assert.Contains(op.Strings, s => s.Content == "[DevTools] ");
    }

    /// <summary>fix #40 quoted 半边收宽：名字嵌在更长字符串字面量内（非 "\"名\"" 整串形态）
    /// ——读轮裸名正则会命中字符串内的名字，replacement 使引号失衡（旧编译器报错）。
    /// 须在守卫层跳过，残留引用走原拒批路径。</summary>
    [Fact]
    public void VanillaEdit_FreshIVar_NameInsideStringLiteral_Rejected()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("fresh_in_str = 5;\nreturn \"see fresh_in_str for details\" + string(fresh_in_str);", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Null(r.Batch);
        Assert.Contains(r.Failures, f => f.Contains("fresh_in_str"));
    }

    /// <summary>fix #40 守卫保留面：真实代码层数组访问（剥串后仍有 '['）照旧跳过——数组壳
    /// （[@...@@NewGMLArray@@...]）会被两轮正则撕碎，保守边界不动。</summary>
    [Fact]
    public void VanillaEdit_FreshIVar_CodeLayerArray_StillRejected()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("var _arr;\n_arr[0] = 3;\nfresh_arr_ivar = 5;\nreturn fresh_arr_ivar + _arr[0];", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Null(r.Batch);
        Assert.Contains(r.Failures, f => f.Contains("fresh_arr_ivar"));
    }

    /// <summary>fix #37（E2E 矩阵 M5 触发，用户批准）：新全局变量（product Global-VARI −
    /// boot Global-VARI 差集判别）的引用改写为官方动态 API——读 variable_global_get(名字)
    /// argc=1；写 variable_global_set(名字, 值) argc=2。与 #36-B 同族（源码层反编译→文本
    /// 替换→重编译），改写后载荷不再引用新全局变量名（CalibCorpus g: 键拒批面解除）。
    /// 正则锚定 global. 前缀——与同名实例变量正交（#36-B 的 lookbehind 已排除点号后名）。</summary>
    [Fact]
    public void VanillaEdit_NewGlobalVar_RewrittenToDynamicApi()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("global.fresh_e2e_gvar = 5;\nreturn global.fresh_e2e_gvar * 2;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Kind == "swap");
        Assert.DoesNotContain(op.Instructions, i => i.Var == "fresh_e2e_gvar");
        Assert.Contains(op.Instructions, i => i.Fn == "variable_global_get" && i.Low16 == 1);
        Assert.Contains(op.Instructions, i => i.Fn == "variable_global_set" && i.Low16 == 2);
        Assert.Contains(op.Strings, s => s.Content == "fresh_e2e_gvar");
    }

    /// <summary>fix #37 槽载荷边界（#36-B 同型）：product-only 槽 entry（新脚本）不走源码层
    /// 重写（ReplaceGML 对 AddFunction wrapper 的子条目结构重排会 IndexOutOfRange），引用新
    /// 全局变量 → 原拒批路径放行（重启游戏后由正常载入覆盖）。</summary>
    [Fact]
    public void NewScriptSlot_NewGlobalVar_Rejected_HonestBoundary()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        Msl.AddFunction("function scr_slot_gvar() { global.fresh_slot_gvar = 41;\nreturn global.fresh_slot_gvar + 1; }", "scr_slot_gvar");
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_slot_gvar();", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Null(r.Batch);   // 整批不推（fail-closed）
        Assert.Contains(r.Failures, f => f.Contains("fresh_slot_gvar") && f.Contains("boot baseline 无来源"));
    }

    /// <summary>fix #40 全局域同修（#37 守卫与 #36-B 同型）：字符串字面量含 '[' 不得连坐
    /// 新全局变量救援（真机形态同 VanillaEdit_FreshIVar_StringBracketLiteral_Rescued）。</summary>
    [Fact]
    public void VanillaEdit_FreshGVar_StringBracketLiteral_Rescued()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        UndertaleCode bootEntry = boot.Code.First(c => c.ParentEntry == null
            && c.Name.Content.StartsWith("gml_Object_")
            && !c.Name.Content.StartsWith("gml_Object_o_msl_"));
        product.Code.First(c => c.Name.Content == bootEntry.Name.Content)
            .ReplaceGML("global.fresh_str_gvar = 5;\nreturn string(global.fresh_str_gvar) + \"[DevTools] \";", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.True(r.Batch != null, "批被拒：" + string.Join("；", r.Failures));
        var op = Assert.Single(r.Batch!.Ops, o => o.Kind == "swap");
        Assert.DoesNotContain(op.Instructions, i => i.Var == "fresh_str_gvar");
        Assert.Contains(op.Instructions, i => i.Fn == "variable_global_set" && i.Low16 == 2);
        Assert.Contains(op.Instructions, i => i.Fn == "variable_global_get" && i.Low16 == 1);
        Assert.Contains(op.Strings, s => s.Content == "[DevTools] ");
    }

    /// <summary>#21 局部变量键域回归（_stagger_chance 类事故形态）：entry 唯一局部名只能由
    /// 被换 entry 自身的 baseline 版供——语料必须含目标 entry 自己（agent 在换入前收割其
    /// 换装前活 buffer，时序安全）。</summary>
    [Fact]
    public void ChangedEntry_UniqueLocal_CoveredByOwnBaseline()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        boot.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0")
            .ReplaceGML("msl_live_apply();\nvar _probe_local;\n_probe_local = 1;", boot);
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0")
            .ReplaceGML("msl_live_apply();\nvar _probe_local;\n_probe_local = 2;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        Assert.Contains(r.Batch!.Ops, o => o.Entry == "gml_Object_o_msl_live_Step_0" && o.Kind == "swap");
        Assert.Contains(r.Batch.CalibOps, c => c.Entry == "gml_Object_o_msl_live_Step_0"
            && c.Variables.Contains("_probe_local"));
    }

    [Fact]
    public void NewScript_FunctionDecl_RoutedToSlot_WithChildCallForm()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        // 真实 mod 形态（TW Codes/*.gml 同款）：function 声明 → wrapper 根 + gml_Script_ 子。
        // 旧裸体形态（"return 42;"）在产品里本就 runtime 不可调用（无 SCPT/子——call 落空），
        // #16b 起槽载荷要求 wrapper 形态。
        Msl.AddFunction("function scr_brand_new() { return 42; }", "scr_brand_new");
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_brand_new();", product);
        var alloc = NewAlloc(boot);
        var r = HotPipeline.BuildBatch(boot, product, alloc,
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        // 槽键统一 gml_Script_ 形态（调用方/脚本自身两处分配不撞键、不双占槽）
        Assert.Equal("msl_slot_0", alloc.ScriptSlots["gml_Script_scr_brand_new"]);
        // 调用点重定向到 gml_Script_ 子条目名（S2②：调用操作数 = 100000+子 codeId——
        // 裸槽名会解析到根 = 绑定尾而非函数体）
        var callerOp = r.Batch!.Ops.First(o => o.Kind == "swap" && o.Entry == "gml_Object_o_msl_live_Step_0");
        Assert.Contains(callerOp.Instructions, i => i.Fn == "gml_Script_msl_slot_0");
        // 脚本自身也换入槽（product-only 发现——计划 2709 行预期；Diff 旧版从不产出）
        var slotOp = r.Batch!.Ops.First(o => o.Entry == "msl_slot_0");
        Assert.Equal("swap", slotOp.Kind);
        // LocalsCount 取 product 偏移 4 子条目值（function(){return 42;} → 0）
        Assert.Equal(0, slotOp.LocalsCount);
        // 尾部两处重写：fnref/self-var 从产品名改到槽名（agent 侧才可解析）
        Assert.Contains(slotOp.Instructions, i => i.Fn == "gml_Script_msl_slot_0");
        Assert.Contains(slotOp.Instructions, i => i.Var == "msl_slot_0");
        Assert.DoesNotContain(slotOp.Instructions, i => i.Fn == "gml_Script_scr_brand_new" || i.Var == "scr_brand_new");
    }

    /// <summary>#16b：loader 三 op 的载荷形态。trigger/restore = 平文本编译（LocalsCount
    /// 自设 = 1+distinct——裸 entry 的编译 LocalsCount=0 是谎，[2d] 实证）；loader = scratch
    /// wrapper 编译（完整 [B][body][exit][tail]，尾部重写到真名，LocalsCount = scratch 子条目值
    /// 与垫片 stub 子=1 精确匹配）。字符串全部 boot 可解析——折叠判例：`"a" + string(字面量)
    /// + "b"` 会被编译器常量折叠成非 boot 整路径字面量（fold-probe A），直接 `"a" + 数字 + "b"`
    /// 不折叠（fold-probe D）。</summary>
    [Fact]
    public void BuildLoaderOps_TriggerPlain_LoaderWrapper_TailRewritten()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var strg = new Dictionary<string, int>();
        for (int i = 0; i < boot.Strings.Count; i++) strg.TryAdd(boot.Strings[i].Content, i);

        string loaderText = LoaderGen.MergeOrdered(new[] {
            LoaderGen.SpriteLoader(new SpriteChange { Name = "spr_x" },
                new StripInfo { Frames = 1, OriginX = 0, OriginY = 0 }, 70000)
        });
        int seq = 0;
        var ops = HotPipeline.BuildLoaderOps(loaderText, boot, strg, ref seq);

        Assert.Equal(3, ops.Count);
        var trigger = ops[0];
        var restore = ops[1];
        var loader = ops[2];
        Assert.Equal("trigger", trigger.Kind);
        Assert.Equal("restore", restore.Kind);
        Assert.Equal("loader", loader.Kind);
        Assert.Equal(LiveStubInjector.ManagerStepEntry, trigger.Entry);
        Assert.Equal(LiveStubInjector.LoaderSlot, loader.Entry);
        Assert.True(trigger.ExecuteOnce);
        Assert.True(restore.ExecuteOnce);

        // trigger/restore：LocalsCount 自设 = 1+distinct（文本 0 局部 → 1 == Step 事件 boot=1）
        Assert.Equal(1, trigger.LocalsCount);
        Assert.Equal(1, restore.LocalsCount);

        // loader：wrapper 形态（首指令 B 跳过 body；含 exit；绑定尾引用真名 msl_loader_0）
        Assert.Equal(0xB6, loader.Instructions[0].Kind);   // Opcode.B（线上契约值 = MslLive.Agent.BcEncoder.OpB）
        Assert.Contains(loader.Instructions, i => i.Kind == 0x9D);   // Opcode.Exit;
        Assert.Contains(loader.Instructions, i => i.Fn == "gml_Script_" + LiveStubInjector.LoaderSlot);
        Assert.Contains(loader.Instructions, i => i.Var == LiveStubInjector.LoaderSlot);
        Assert.DoesNotContain(loader.Instructions, i => i.Fn != null && i.Fn.Contains("scratch"));
        Assert.DoesNotContain(loader.Instructions, i => i.Var != null && i.Var.Contains("scratch"));
        // LocalsCount = scratch 子条目值（loader 体 _t 一个局部 → 1，与垫片 stub 子=1 匹配）
        Assert.Equal(1, loader.LocalsCount);
        // loader 字符串全部 boot 可解析（fold-probe：路径拼接不得折叠成整路径字面量）
        Assert.All(loader.Strings, s => Assert.True(s.StrgIndex >= 0,
            $"non-boot string '{s.Content}' (folded or unseeded)"));
        // scratch 根+子用毕清理（boot.Code 无 msl_scratch_ 前缀残留；
        // 不能查子串 "scratch"——vanilla 有 o_enemy_pass_tis_but_a_scratch 之类的原生名）
        Assert.DoesNotContain(boot.Code, c => c.Name.Content.StartsWith("msl_scratch_"));
    }

    [Fact]
    public void NewString_PassesThroughForAgentReject() { /* 取舍清单1：MSL 透传，agent 拒绝——本用例在 Part 2 覆盖 */ }

    // ---- fix #29（15:09 真机形态）----

    static NamedPipeServerStream NewPipeServer(string pipe)
    {
        var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task.Run(() => s.WaitForConnection());
        return s;
    }
    static void WaitPipeConnected(NamedPipeServerStream s)
    {
        for (int i = 0; i < 2000 && !s.IsConnected; i++) Thread.Sleep(5);
        if (!s.IsConnected) throw new InvalidOperationException("mock agent: client never connected");
    }

    /// <summary>造一条窗口记录：Register 只对文件做 SHA——小文件互异内容即互异哈希，
    /// 不必复制 200MB vanilla（Product 侧共用同一份解析产物）。</summary>
    static CompileRecord SeedRecord(UndertaleData product, string tag)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"msl_b29_{tag}_{Guid.NewGuid():N}.win");
        File.WriteAllText(tmp, tag + Guid.NewGuid().ToString("N"));
        return BaselineStore.Register(product, tmp, new List<LiveTextureEntry>());
    }

    /// <summary>fix #29（15:09 真机形态）：窗口满载且 boot 记录在最旧位时，同一次点击的
    /// Register 不得先于 lock——否则把本次要锁的 boot 基线挤出 3 条窗口 → LockBaseline
    /// 落空 →「不在本会话基线窗口」误拒（差一个身位）。重排后 lock 先行（③ 窗口命中即
    /// pin 出窗，此后逐出不可触），Register 后置；且注册仍发生（「编译过就有记录」——
    /// 未来 boot 依赖不回归）。mock agent（真管道）上报 boot 哈希模拟游戏 hello。</summary>
    [Fact]
    public void BuildAndPush_RegistersAfterLock_FullWindowKeepsBootRecord()
    {
        Main.Settings.DevMode = true;
        var bootProduct = Load();
        LiveStubInjector.Inject(bootProduct, new LiveQuotas());
        var recBoot = SeedRecord(bootProduct, "boot");
        SeedRecord(bootProduct, "x");
        SeedRecord(bootProduct, "y");                  // 窗口 [boot, x, y]：满载，boot 在最旧位
        // 本次点击的落盘产物（内容互异 → 哈希互异）
        DataLoader.savedDataPath = Path.Combine(Path.GetTempPath(), $"msl_b29_click_{Guid.NewGuid():N}.win");
        File.WriteAllText(DataLoader.savedDataPath, "click-product");
        string clickHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(DataLoader.savedDataPath)));

        string pipe = "msl-test-" + Guid.NewGuid().ToString("N");
        using var server = NewPipeServer(pipe);
        var agent = Task.Run(() =>
        {
            WaitPipeConnected(server);
            Wire.Send(server, "hello", new HelloMsg
                { AgentVersion = "v1", Pid = 1, BootHash = recBoot.Hash, StubPresent = true, AgentStatus = "ok" });
            var ack = Wire.Receive(server);
            if (!Wire.Decode<HelloAck>(ack.Data).Accept) return;   // 旧序（注册先于锁）在此被拒
            Wire.Receive(server);   // vars
            Wire.Receive(server);   // proof
            Wire.Send(server, "proofAck", new ProofAck { Ok = true, Verified = 64 });
            var q = Wire.Receive(server);
            Assert.Equal("queryBlanks", q.Type);
            Wire.Send(server, "blanks", new BlanksMsg { SpriteFirst = 100, PathFirst = 8 });
        });
        LiveSession.ForRunningGameOverride = (quotas, shells) =>
            new LiveSession(pipe, () => "v1", () => "2022.9.0.0 bc17", () => new List<(string, string)>(),
                quotas, shells);

        var r = HotPipeline.BuildAndPush(bootProduct, DataLoader.savedDataPath);

        Assert.True(r.Attempted, "同点击注册把 boot 基线挤出窗口（15:09 形态）：" + string.Join("；", r.Failures));
        Assert.True(r.Succeeded);                          // 同一份 vanilla → 空 batch（无变更）
        Assert.Same(recBoot, BaselineStore.BootBaseline);   // 锁的是 boot 记录本尊（pin 出窗）
        Assert.Equal(clickHash, BaselineStore.Latest!.Hash);   // 注册仍发生（未来 boot 依赖）
        agent.Wait();
    }

    /// <summary>fix #31（09-04 19:31 真机形态）：BuildAndPush 的契约 = 任何一步失败降级纯写盘
    /// （写盘已成功，见方法注释），但 Register/BuildBatch/PushBatch 一带的未捕获异常会直穿
    /// CompileDataWinFlow 的 fire-and-forget Task 无声蒸发——零日志零弹窗，还吞掉尾部
    /// vallina 重载弄脏编译基底（次生：下轮 o_msl_timer already exists）。兜底捕获后必须
    /// 以 Failures 形态返回，绝不抛出。注入向量：无游戏连接（override 直接抛）→ 早退 catch
    /// 里的 Register 对不存在的产物路径炸 FileNotFoundException——修复前它从 catch 块里
    /// 直接逃出方法。</summary>
    [Fact]
    public void BuildAndPush_InternalThrow_NeverEscapes_DegradesToFailure()
    {
        Main.Settings.DevMode = true;
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        // ResDirAbs 读全局 savedDataPath（非参数）——给个真实存在的临时产物路径让它过
        DataLoader.savedDataPath = Path.Combine(Path.GetTempPath(), $"msl_b31_ctx_{Guid.NewGuid():N}.win");
        File.WriteAllText(DataLoader.savedDataPath, "ctx");
        LiveSession.ForRunningGameOverride = (quotas, shells) =>
            throw new InvalidOperationException("测试：无游戏连接");
        try
        {
            var r = HotPipeline.BuildAndPush(product,
                Path.Combine(Path.GetTempPath(), "msl_b31_missing_product.win"));
            Assert.False(r.Succeeded);
            Assert.Contains(r.Failures, f => f.Contains("热通道异常"));
        }
        finally { LiveSession.ForRunningGameOverride = null; }
    }
}
