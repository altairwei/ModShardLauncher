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
    public void NewScript_RewrittenToSlot()
    {
        var boot = Load();
        LiveStubInjector.Inject(boot, new LiveQuotas());
        var product = Load();
        LiveStubInjector.Inject(product, new LiveQuotas());
        Msl.AddFunction("return 42;", "scr_brand_new");   // DataLoader.data == product（Load 已指）
        var caller = product.Code.First(c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        caller.ReplaceGML("msl_live_apply();\nscr_brand_new();", product);
        var alloc = NewAlloc(boot);
        var r = HotPipeline.BuildBatch(boot, product, alloc,
            new List<LiveTextureEntry>(), new List<LiveTextureEntry>());
        Assert.NotNull(r.Batch);
        Assert.Equal("msl_slot_0", alloc.ScriptSlots["scr_brand_new"]);
        var callerOp = r.Batch!.Ops.First(o => o.Kind == "swap");
        Assert.Contains(callerOp.Instructions, i => i.Fn == "msl_slot_0");
    }

    [Fact]
    public void NewString_PassesThroughForAgentReject() { /* 取舍清单1：MSL 透传，agent 拒绝——本用例在 Part 2 覆盖 */ }
}
