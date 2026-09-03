using System.IO.Pipes;
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

    /// <summary>#21 fail-closed：编辑引入 baseline 从未见过的变量名 → 整批不推 + 原因含变量名
    /// 与重启指引（_ally_hp 事故的正解——宁可拒批也不带模拟值错装）。</summary>
    [Fact]
    public void ChangedEntry_NewVariableName_RejectedHonestly()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        var target = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        target.ReplaceGML("msl_live_apply();\nglobal.msl_never_seen_xyz = 1;", product);
        var r = HotPipeline.BuildBatch(boot, product, NewAlloc(boot),
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.Null(r.Batch);
        Assert.Contains(r.Failures, f => f.Contains("msl_never_seen_xyz") && f.Contains("重启"));
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
}
