using ModShardLauncher;
using ModShardLauncher.HotReload;
using MslLive.Shared;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class HotPipelineTests : IDisposable
{
    readonly UndertaleData? savedData;

    public HotPipelineTests() => savedData = DataLoader.data;
    public void Dispose() { if (savedData != null) DataLoader.data = savedData; }

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
    }

    [Fact]
    public void ChangedEntry_ProducesSwapOp()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var target = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        target.ReplaceGML("msl_live_apply();\nglobal.msl_dbg = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        var op = Assert.Single(r.Batch!.Ops, o => o.Entry == "gml_Object_o_msl_live_Step_0");
        Assert.Equal("swap", op.Kind);
        Assert.Contains("msl_dbg", op.Variables);
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
}
