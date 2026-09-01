using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class LiveStubInjectorTests : IDisposable
{
    readonly UndertaleData? savedData;

    public LiveStubInjectorTests() => savedData = DataLoader.data;
    public void Dispose() { if (savedData != null) DataLoader.data = savedData; }

    static UndertaleData Load()
    {
        UndertaleData data;
        using (var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });
        // Msl.* 原语硬绑定 ModLoader.Data（= DataLoader.data）——指到测试装载的 data
        DataLoader.data = data;
        return data;
    }

    [Fact]
    public void Inject_CreatesAllStructures()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);

        for (int i = 0; i < q.ScriptSlots; i++)
            Assert.Contains(data.Code, c => c.Name.Content == $"msl_slot_{i}");
        foreach (var n in new[] { "msl_live_apply", "msl_live_report", "msl_loader_0" })
            Assert.Contains(data.Code, c => c.Name.Content == n);

        // TheWitcher 形状（fix-loop #16 根因修复，形状真源 = 真机可用的 TW 产物 forensics）：
        // 每个 stub 脚本 = 根 Code + gml_Script_ 子条目(Offset=4) + prefixed SCPT→子 + prefixed
        // Functions + 裸名 VARI。根因：裸语句 GML（"return 0;"）不走编译器 isNewFunc 路径，
        // 五索引残缺（无子/SCPT 指根/无 VARI）→ 运行时 call fn 无法解析 → "call to
        // non-existent script"。TW 的脚本根不在 GlobalInit——调用一律走 call fn='gml_Script_X'
        // （Functions 名直呼），34555ad 的手动 SCPT+GlobalInit 注册已删。
        var stubNames = new[] { "msl_live_apply", "msl_live_report", "msl_loader_0" }
            .Concat(Enumerable.Range(0, q.ScriptSlots).Select(i => $"msl_slot_{i}"));
        foreach (var n in stubNames)
        {
            var root = data.Code.First(c => c.Name.Content == n);
            var child = data.Code.Single(c => c.Name.Content == "gml_Script_" + n);
            Assert.Equal(root, child.ParentEntry);
            Assert.Contains(child, root.ChildEntries);
            Assert.Equal(4u, child.Offset);
            // 子紧跟根（写入器全局游标编址：child 必须紧邻其父，否则 blob 地址被劫持——#4）
            Assert.Equal(data.Code.IndexOf(root) + 1, data.Code.IndexOf(child));
            var scpt = data.Scripts.Single(s => s.Name.Content == "gml_Script_" + n);
            Assert.Equal(child, scpt.Code);
            Assert.Contains(data.Functions, f => f.Name.Content == "gml_Script_" + n);
            Assert.Contains(data.Variables, v => v.Name.Content == n);
            // 裸名 SCPT 不存在；根不进 GlobalInit（TW 形状）
            Assert.DoesNotContain(data.Scripts, s => s.Name.Content == n);
            Assert.DoesNotContain(data.GlobalInitScripts, g => g.Code == root);
        }

        var manager = data.GameObjects.First(o => o.Name.Content == "o_msl_live");
        Assert.True(manager.Persistent);
        Assert.Contains(manager.Events[(int)EventType.Step], e => e.EventSubtype == 0);
        Assert.Contains(manager.Events[(int)EventType.Other],
            e => e.EventSubtype == (uint)EventSubtypeOther.GameStart);

        // loader 路径拼接用字符串已播种（loader GML 只允许 boot 字符串）
        Assert.Contains(data.Strings, s => s.Content == "mods/_live/res/");
        Assert.Contains(data.Strings, s => s.Content == ".png");

        var start = data.Rooms.First(r => r.Name.Content == "START");
        Assert.Contains(start.GameObjects, g => g.ObjectDefinition?.Name?.Content == "o_msl_live");

        for (int i = 0; i < q.ShellObjects; i++)
        {
            var shell = data.GameObjects.First(o => o.Name.Content == $"o_msl_shell_{i}");
            if (q.ShellParents[i] != "")
                Assert.Equal(q.ShellParents[i], shell.ParentId?.Name?.Content);
            Assert.Contains(shell.Events[(int)EventType.Create], e => e.EventSubtype == 0);
            Assert.Contains(shell.Events[(int)EventType.Other], e => e.EventSubtype == 10);
        }

        for (int i = 0; i < q.EmptyRooms; i++)
            Assert.Contains(data.Rooms, r => r.Name.Content == $"r_msl_empty_{i}");
        Assert.True(q.RoomBaseIndex > 0);

        // step 事件的 msl_live_apply 调用必须解析到 gml_Script_ 子函数（编译期绑定检查；
        // TW 宿主同款形态：Call fn='gml_Script_X'——KnownSubFunctions 解析）
        var stepCode = data.Code.First(c => c.Name.Content == LiveStubInjector.ManagerStepEntry);
        Assert.Contains(stepCode.Instructions,
            i => i.Function?.Target?.Name?.Content == "gml_Script_" + LiveStubInjector.ApplyFn);
    }

    /// <summary>#18（真机 22:43 实证：AcquireBlanks 5×-1、「report calibrated」全历史零次）：
    /// GMS2.3 运行时只从 Layer 侧创建房间实例——vanilla START 两实例两侧镜像、若两侧都读
    /// vanilla 实例会双生；TW initializer 双侧写入（AddGameObject，多年可用）。旧代码只写
    /// legacy GameObjects（与 ModLoader.o_ScriptEngine 同款 bug）→ o_msl_live 从未生成 →
    /// GameStart/Step 从未跑 → blank 分配/上报/校准全链死。</summary>
    [Fact]
    public void Inject_ManagerInstancePlacedInInstancesLayer()
    {
        var data = Load();
        LiveStubInjector.Inject(data, new LiveQuotas());

        var start = data.Rooms.First(r => r.Name.Content == "START");
        var layer = start.Layers.Single(l => l.LayerType == UndertaleRoom.LayerType.Instances);
        Assert.Contains(layer.InstancesData.Instances,
            g => g.ObjectDefinition?.Name?.Content == "o_msl_live");
    }

    /// <summary>#18 自愈：历史产物里 manager 是 legacy-only 残留（旧代码病灶形态）——
    /// 重注入后必须迁入 Layer 侧且 legacy 不重复（幂等以 Layer 侧为准）。</summary>
    [Fact]
    public void Inject_HealsLegacyOnlyManagerInstance()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);

        // 人为打回 #18 病灶形态：实例从 Layer 侧摘掉、legacy 侧保留
        var start = data.Rooms.First(r => r.Name.Content == "START");
        var layer = start.Layers.Single(l => l.LayerType == UndertaleRoom.LayerType.Instances);
        var placed = layer.InstancesData.Instances
            .Single(g => g.ObjectDefinition?.Name?.Content == "o_msl_live");
        layer.InstancesData.Instances.Remove(placed);

        LiveStubInjector.Inject(data, q);   // 再注入 = 自愈

        Assert.Contains(layer.InstancesData.Instances,
            g => g.ObjectDefinition?.Name?.Content == "o_msl_live");
        Assert.Equal(1, start.GameObjects.Count(
            g => g.ObjectDefinition?.Name?.Content == "o_msl_live"));   // legacy 不重复
    }

    /// <summary>dummy stub（"function X() { return 0; }"）的编译形态钉版：Task 14 的
    /// Trampoline 收尾字节校验以它为基准。wrapper 形态 = [B 跳过 body 到绑定尾][body][绑定尾]，
    /// 子条目 Offset=4 → child 从 body 第一条执行。body = pushi.e 0 + conv.i.v + ret.v
    /// （12 字节，探针实测）。编译器输出若变，本测试红 = MslLive.Test 的 Trampoline fixture
    /// 同步过期。</summary>
    [Fact]
    public void StubDummy_CompilesTo_WrapperWithPushiZeroRetVBody()
    {
        var data = Load();
        LiveStubInjector.Inject(data, new LiveQuotas());
        var root = data.Code.First(c => c.Name.Content == LiveStubInjector.ApplyFn);
        // wrapper 首 instruction = B（root 执行时跳过 body 直达绑定尾；child @+4 落在 body 头）
        Assert.Equal(UndertaleInstruction.Opcode.B, root.Instructions[0].Kind);
        // body（child 入口，blob +4）= pushi.e 0 → conv.i.v → ret.v（TW msl_print 同构）
        var push = root.Instructions[1];
        Assert.Equal(UndertaleInstruction.Opcode.PushI, push.Kind);
        Assert.Equal((short)0, Assert.IsType<short>(push.Value));
        var conv = root.Instructions[2];
        Assert.Equal(UndertaleInstruction.Opcode.Conv, conv.Kind);
        Assert.Equal(UndertaleInstruction.DataType.Int32, conv.Type1);
        Assert.Equal(UndertaleInstruction.DataType.Variable, conv.Type2);
        var ret = root.Instructions[3];
        Assert.Equal(UndertaleInstruction.Opcode.Ret, ret.Kind);
        Assert.Equal(UndertaleInstruction.DataType.Variable, ret.Type1);
        // 绑定尾引用 gml_Script_ 子函数（push.i fnref —— TW msl_print 绑定尾同款）
        Assert.Contains(root.Instructions, i =>
            i.Value is UndertaleInstruction.Reference<UndertaleFunction> rf &&
            rf.Target?.Name?.Content == "gml_Script_" + LiveStubInjector.ApplyFn);
        var child = data.Code.Single(c => c.Name.Content == "gml_Script_" + LiveStubInjector.ApplyFn);
        Assert.Equal(4u, child.Offset);
    }

    /// <summary>垫片矩阵钉版（#16b，LocalsCount 语义 = AssemblyWriter「+1 for arguments」
    /// 公式，仅 LOCZ 存在时更新——Msl.AddCode 预建）：loader/slot stub 垫 `var _t = 0;`
    /// （子=1：loader 载荷 _t 精确匹配、shell-config-only 0≤1）；apply/report 不垫（trampoline
    /// 0 局部=0≤0 精确）；壳事件与 RoomCC 垫 2 var 余量（boot=3：payload=product 事件代码
    /// 原样可有 ≤2 局部）；Step 事件不垫（boot=1：trigger 载荷 1+0=1 精确）。</summary>
    [Fact]
    public void Inject_PadMatrix_ChildLocalsCountsPinned()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);

        // loader/slot：垫 var _t → 子 LocalsCount=1（tw-shape [2c] 垫片探针实证）
        foreach (var n in new[] { "msl_loader_0" }.Concat(Enumerable.Range(0, q.ScriptSlots).Select(i => $"msl_slot_{i}")))
            Assert.Equal(1u, data.Code.Single(c => c.Name.Content == "gml_Script_" + n).LocalsCount);
        // apply/report：不垫 → 子 LocalsCount=0（tw-shape [2] 实证）
        foreach (var n in new[] { "msl_live_apply", "msl_live_report" })
            Assert.Equal(0u, data.Code.Single(c => c.Name.Content == "gml_Script_" + n).LocalsCount);
        // Step 事件：不垫 → LocalsCount=1（AddCode 公式 0+1；trigger 载荷 1≤1）
        Assert.Equal(1u, data.Code.First(c => c.Name.Content == LiveStubInjector.ManagerStepEntry).LocalsCount);
        // 壳事件：垫 2 var → LocalsCount=3（0 局部事件公式 2+1）
        var shell = data.GameObjects.First(o => o.Name.Content == "o_msl_shell_0");
        Assert.All(shell.Events.SelectMany(e => e),
            ev => Assert.Equal(3u, ev.Actions[0].CodeId.LocalsCount));
        // RoomCC：垫 2 var → LocalsCount=3
        Assert.Equal(3u, data.Code.First(c => c.Name.Content == "gml_RoomCC_r_msl_empty_0_0").LocalsCount);
    }

    [Fact]
    public void Inject_Twice_IsIdempotent()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);
        int codeCount = data.Code.Count, objCount = data.GameObjects.Count, roomCount = data.Rooms.Count;
        int scptCount = data.Scripts.Count, initCount = data.GlobalInitScripts.Count;
        LiveStubInjector.Inject(data, q);
        Assert.Equal(codeCount, data.Code.Count);
        Assert.Equal(objCount, data.GameObjects.Count);
        Assert.Equal(roomCount, data.Rooms.Count);
        Assert.Equal(scptCount, data.Scripts.Count);
        Assert.Equal(initCount, data.GlobalInitScripts.Count);
    }

    /// <summary>自愈路径：在「裸根无子条目」的历史产物（34555ad 及更早编译的 data.win：
    /// 裸根 + 裸 SCPT + GlobalInit 注册）上重跑 Inject，必须重编译为 function 声明形态
    /// （TheWitcher 形状）且根不重复。</summary>
    [Fact]
    public void Inject_HealsLegacyBareRootIntoWrapperShape()
    {
        var data = Load();
        Msl.AddFunction("return 0;", LiveStubInjector.ApplyFn);   // 模拟旧产物：只有裸 code entry
        LiveStubInjector.Inject(data, new LiveQuotas());
        Assert.Single(data.Code.Where(c => c.Name.Content == LiveStubInjector.ApplyFn));   // 根不重复
        var root = data.Code.First(c => c.Name.Content == LiveStubInjector.ApplyFn);
        var child = data.Code.Single(c => c.Name.Content == "gml_Script_" + LiveStubInjector.ApplyFn);
        Assert.Equal(root, child.ParentEntry);
        Assert.Equal(4u, child.Offset);
        var scpt = data.Scripts.Single(s => s.Name.Content == "gml_Script_" + LiveStubInjector.ApplyFn);
        Assert.Equal(child, scpt.Code);
        // 34555ad 形态的 GlobalInit 注册不再补（TW 根不在 GlobalInit）
        Assert.DoesNotContain(data.GlobalInitScripts, g => g.Code == root);
    }

    /// <summary>回归（Task 16 fix-loop #4，真机 Code Error 根因）：GMS2.3 的 child 条目
    /// （gml_Script_* wrapper）不序列化 ParentEntry，靠“地址与父共享”在读侧推断；写入器
    /// （UTMT 0.6.1.0）给 child 编址用的是全局游标 LastBytecodeAddress = 最近写入 blob 的
    /// root——因此 child 在 Code 列表里必须紧跟其父。AddFunction 旧版每次调用把列表头部
    /// 条目轮转到尾部，将 vanilla 的 [gml_GlobalScript_X, gml_Script_X] 父子对拆散并与
    /// 新增槽位交错：保存后 37 个 wrapper 被劫持为 msl_slot_* 的子体（字节码=12 字节
    /// "return 0;" 桩），scr_presets_init(gml_Script_scr_preset_encounter) 返回 undefined
    /// → "Data structure with index does not exist"。注入 → 保存 → 重载，
    /// vanilla 条目的 (Length, ParentEntry) 必须逐一原样保留。</summary>
    [Fact]
    public void Inject_RoundTrip_PreservesVanillaChildEntryOwnership()
    {
        var data = Load();
        var baseline = data.Code.ToDictionary(
            c => c.Name.Content,
            c => (len: c.Length, parent: c.ParentEntry?.Name.Content));
        UndertaleData reloaded;

        LiveStubInjector.Inject(data, new LiveQuotas());

        // 往返走磁盘文件（MemoryStream 会让 vendored UndertaleIO.Read 抛 NRE，且真机
        // 场景本身就是文件读写——以文件为准）
        string tmp = Path.Combine(Path.GetTempPath(), "msl-roundtrip-" + Guid.NewGuid().ToString("N") + ".win");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                UndertaleIO.Write(fs, data);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                reloaded = UndertaleIO.Read(fs, _ => { });
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        var stolen = reloaded.Code
            .Where(c => baseline.TryGetValue(c.Name.Content, out var b)
                        && (b.len != c.Length || b.parent != c.ParentEntry?.Name.Content))
            .ToList();
        Assert.True(stolen.Count == 0,
            string.Join("; ", stolen.Take(5).Select(c =>
                $"{c.Name.Content}: len {baseline[c.Name.Content].len}->{c.Length}, " +
                $"parent '{baseline[c.Name.Content].parent}'->'{c.ParentEntry?.Name.Content}'")));
        // fix-loop #16：我们自己的 gml_Script_ 子条目也必须过读侧推断关——子与父共享
        // blob 地址 + 紧跟父（写入器全局游标编址 = #4 的劫持机制对我们自己的子条目同样生效）
        var mslRoots = reloaded.Code.Where(c => c.Name.Content.StartsWith("msl_")).ToList();
        Assert.NotEmpty(mslRoots);
        Assert.All(mslRoots, root =>
        {
            var child = Assert.Single(root.ChildEntries);
            Assert.Equal("gml_Script_" + root.Name.Content, child.Name.Content);
            Assert.Equal(root, child.ParentEntry);
            Assert.Equal(4u, child.Offset);
            Assert.Equal(reloaded.Code.IndexOf(root) + 1, reloaded.Code.IndexOf(child));
        });
    }
}
