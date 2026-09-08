using ModShardLauncher.HotReload;
using ModShardLauncher.Mods;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 5] 静态 pass 幂等性：同图连跑两遍 → 第二遍后图与第一遍后图等价。
/// 真源：v1 每编译都从 fresh vanilla 开跑，重复永不自现；快推在同一工作图上重放
/// PatchFile——任何无守卫的注入都会逐轮增殖（或按 (obj,type,sub) 重添时直接抛）。
/// 守卫三件套：结构 add 按名 / 事件 add 按 (obj,type,sub) / 文本注入按哨兵子串
/// （FastText.Read 读现图查「本 MSL 注入在不在」）。
/// 诚实边界：PatchMods 全链需 ModInfos UI 宿主不可单测——矩阵覆盖 headless 可跑的
/// 全部 pass；LoadWeapon 经 PatchMods 不可达但可直接调（计划判其仅可代码审读，此处实测）。
/// golden 不受影响：全量编译仍从精源开跑，守卫永不命中。</summary>
[Collection("vanilla")]
public class StaticPassIdempotencyTests : IDisposable
{
    readonly UndertaleData? savedData;
    // LoadWeapon 会向静态表插行（Initalize 换列表引用）——原引用存还原位
    readonly List<string> savedWeapons = ModLoader.Weapons;
    readonly List<string> savedWeaponDescriptions = ModLoader.WeaponDescriptions;

    public StaticPassIdempotencyTests()
    {
        savedData = DataLoader.data;
        FastPushContext.ResetForTest();
    }

    public void Dispose()
    {
        if (savedData != null) DataLoader.data = savedData;
        FastPushContext.ResetForTest();
        FinalTextStore.ResetRound();
        CompileLedger.Clear();
        LootUtils.ResetLootTables();
        ModLoader.Weapons = savedWeapons;
        ModLoader.WeaponDescriptions = savedWeaponDescriptions;
    }

    static UndertaleData Load()
    {
        UndertaleData data;
        using (var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });
        // Msl.* 原语硬绑定 ModLoader.Data（= DataLoader.data）——指到测试装载的 data
        DataLoader.data = data;
        return data;
    }

    static int CountOccurrences(string text, string token) => text.Split(token).Length - 1;

    /// <summary>测试侧直读图真值（DecompileCallAudit 分类：测试侧自读不经 FastText）。</summary>
    static string DecompileEntry(UndertaleData g, string name)
    {
        var entry = g.Code.First(c => c.Name.Content == name);
        return Decompiler.Decompile(entry, new GlobalDecompileContext(g, false));
    }

    // —— 1. InjectLog（PatchFile 头部，每轮必跑）——

    [Fact]
    public void InjectLog_Twice_NoDuplication()
    {
        var g = Load();
        LogUtils.InjectLog();
        var s1 = SnapshotLog(g);
        LogUtils.InjectLog();   // 现状：第二轮 AddNewEvent 同 (obj,type,sub) 直接抛
        var s2 = SnapshotLog(g);
        Assert.Equal(s1, s2);
    }

    static (int FnRoots, int TimerCreateEvents, int TimerStepEvents, int LogCreateEvents, int LoaderLines) SnapshotLog(UndertaleData g)
    {
        var timer = g.GameObjects.First(o => o.Name.Content == "o_msl_timer");
        var log = g.GameObjects.First(o => o.Name.Content == "o_msl_log");
        return (
            g.Code.Count(c => c.Name.Content is "scr_msl_log" or "scr_msl_log_save"),
            timer.Events[(int)EventType.Create].Count,
            timer.Events[(int)EventType.Step].Count,
            log.Events[(int)EventType.Create].Count,
            CountOccurrences(DecompileEntry(g, "gml_Object_o_gameLoader_Create_0"), "o_msl_log"));
    }

    // —— 2. InjectLootScripts（PatchFile 尾部，有表即跑）——

    [Fact]
    public void InjectLootScripts_Twice_NoDuplication()
    {
        var g = Load();
        var items = new ItemsTable(new[] { "msl_test_item" }, new[] { 1 }, new[] { 1 });
        Msl.AddLootTable("msl_test_loot", items, 0, 0, 0, new RandomItemsTable(new[] { 1 }, items));
        // 前置补桩（非断言放宽）：InjectLootScripts 对 vanilla 0.9.4.25 有两处 v1 遗留过时
        // 引用（T5 探针实证）——① 首链目标 gml_Object_o_chest_p_Alarm_1：o_chest_p 对象
        // 已不存在；② 次链目标 gml_Object_c_container_Other_10：条目在但体内已无
        // script_execute 行（MatchFrom 无匹配即抛）。均属既有缺陷、与幂等化无关——换目标/
        // 换锚行属功能决策，待用户裁决。此处在测试私有图副本里播种可解析桩使双跑判据可测；
        // 断言全强度不降。
        SeedMatchableEntry(g, "gml_Object_o_chest_p_Alarm_1");
        SeedMatchableEntry(g, "gml_Object_c_container_Other_10");

        LootUtils.InjectLootScripts();
        var s1 = SnapshotLoot(g);
        LootUtils.InjectLootScripts();   // 现状：同名 AddFunction 重复根 + 三处注入行翻倍
        var s2 = SnapshotLoot(g);
        Assert.Equal(s1, s2);
    }

    /// <summary>桩条目 = 体内含 script_execute 调用（MatchFrom 锚）。既有同名条目先摘
    ///（测试私有副本内的前置整理，图不落盘）；条目名必须与 InjectLootScripts 的目标一致。</summary>
    static void SeedMatchableEntry(UndertaleData g, string name)
    {
        var old = g.Code.FirstOrDefault(c => c.Name.Content == name);
        if (old != null)
        {
            g.Code.Remove(old);
            var cl = g.CodeLocals.FirstOrDefault(l => l.Name?.Content == name);
            if (cl != null) g.CodeLocals.Remove(cl);
        }
        Msl.AddCode("script_execute(scr_loot_chest_p);", name);
    }

    static (int Fns, int ChestLines, int ContainerLines, int UnitLines) SnapshotLoot(UndertaleData g) => (
        g.Code.Count(c => c.Name.Content.StartsWith("scr_msl_resolve_")),
        CountOccurrences(DecompileEntry(g, "gml_Object_o_chest_p_Alarm_1"), "scr_msl_resolve_loot_table"),
        CountOccurrences(DecompileEntry(g, "gml_Object_c_container_Other_10"), "scr_msl_resolve_loot_table"),
        CountOccurrences(DecompileEntry(g, "gml_Object_o_unit_Destroy_0"), "scr_msl_resolve_loot_table"));

    // —— 3. PatchMods 尾部三连（AddDisclaimerRoom / ChainDisclaimerRooms / CreateMenu）——

    [Fact]
    public void DisclaimerAndMenuPasses_Twice_NoDuplication()
    {
        var g = Load();
        RunDisclaimerAndMenuPass();
        var s1 = SnapshotDisclaimerMenu(g);
        RunDisclaimerAndMenuPass();   // 现状：AddNewEvent(Draw) 抛 / AddRoom·AddGameObject·InsertNewMenu 增殖
        var s2 = SnapshotDisclaimerMenu(g);
        Assert.Equal(s1, s2);
    }

    /// <summary>PatchMods 尾部（ModLoader.cs:215-217）的最小 headless 形态。</summary>
    static void RunDisclaimerAndMenuPass()
    {
        var room = Msl.AddDisclaimerRoom(new[] { "TestMod" }, new[] { "Author" });
        var overlay = room.GetGameObject("NewInstancesLayer", "o_init_overlay");
        Msl.ChainDisclaimerRooms(new List<(string, UndertaleRoom.GameObject)> { ("r_msl_mod_disclaimer", overlay) });
        Msl.CreateMenu(new List<Menu> {
            new("Test Menu", new UIComponent[] { new("Test Opt", "msl_test_opt", UIComponentType.CheckBox, 1) })
        });
    }

    static (int Rooms, int DrawEvents, int LayerInstances, int CcEntries, int MenuOther10,
        int ComponentObjs, int MenuLines) SnapshotDisclaimerMenu(UndertaleData g)
    {
        var room = g.Rooms.FirstOrDefault(r => r.Name.Content == "r_msl_mod_disclaimer")!;
        var disc = g.GameObjects.First(o => o.Name.Content == "o_msl_mod_disclaimer");
        var menu = g.GameObjects.First(o => o.Name.Content == "o_msl_menu_mod");
        return (
            g.Rooms.Count(r => r.Name.Content == "r_msl_mod_disclaimer"),
            disc.Events[(int)EventType.Draw].Count,
            room.Layers.First(l => l.LayerName.Content == "NewInstancesLayer").InstancesData.Instances.Count,
            g.Code.Count(c => c.Name.Content != null && c.Name.Content.StartsWith("disclaimer_creation_")),
            menu.Events[(int)EventType.Other].Count(e => e.EventSubtype == 10),
            g.GameObjects.Count(o => o.Name.Content != null && o.Name.Content.StartsWith("o_msl_component_")),
            CountOccurrences(DecompileEntry(g, "gml_Object_o_settings_menu_Create_0"), "o_msl_menu_mod"));
    }

    // —— 4/5. LiveStubInjector.Inject（Dev 模式静态 pass；Ensure* 已守卫 = 结构锚，
    //          EnsureEventContent 内容寻址重编 = 账本跟随）——

    [Fact]
    public void LiveStubInjector_Twice_StructureStable()
    {
        var g = Load();
        var q = new LiveQuotas { ScriptSlots = 2, ShellObjects = 2, EmptyRooms = 1, BlankSprites = 4, BlankPaths = 2, ShellParents = new[] { "", "" } };
        LiveStubInjector.Inject(g, q);
        var s1 = SnapshotLive(g);
        LiveStubInjector.Inject(g, q);
        var s2 = SnapshotLive(g);
        Assert.Equal(s1, s2);   // Ensure* 存在性守卫的回归锚（守卫被误删时转红）
    }

    static (int Slots, int Shells, int Rooms, int ManagerEvents) SnapshotLive(UndertaleData g) => (
        g.Code.Count(c => c.Name.Content.StartsWith("msl_slot_") || c.Name.Content == "msl_loader_0"
            || c.Name.Content == "msl_live_apply" || c.Name.Content == "msl_live_report"),
        g.GameObjects.Count(o => o.Name.Content.StartsWith("o_msl_shell_")),
        g.Rooms.Count(r => r.Name.Content.StartsWith("r_msl_empty_")),
        g.GameObjects.First(o => o.Name.Content == "o_msl_live").Events.Sum(e => e.Count));

    [Fact]
    public void LiveStubInjector_Replay_ChangedQuota_RecompilesAndLedgerTracks()
    {
        var g = Load();
        var q1 = new LiveQuotas { ScriptSlots = 2, ShellObjects = 1, EmptyRooms = 1, BlankSprites = 4, BlankPaths = 2, ShellParents = new[] { "" } };
        var q2 = new LiveQuotas { ScriptSlots = 2, ShellObjects = 1, EmptyRooms = 1, BlankSprites = 8, BlankPaths = 2, ShellParents = new[] { "" } };

        FastPushContext.BeginPush();
        LiveStubInjector.Inject(g, q1);
        FastPushContext.EndPush();

        FastPushContext.BeginPush();
        LiveStubInjector.Inject(g, q2);   // 配额变化 → GameStart 体变 → 直编重编且账本须跟上
        FastPushContext.EndPush();

        string gs = Msl.EventName(LiveStubInjector.ManagerObject, EventType.Other, (uint)EventSubtypeOther.GameStart);
        string gsQ2 = LiveStubInjector.GameStartGml(q2.BlankSprites, q2.BlankPaths);
        Assert.True(CompileLedger.IsCurrent(gs, TextHash.Hash(gsQ2)),
            "EnsureEventContent 重编后账本未记账（下轮同体无法跳编）");

        // 同体重放（第三轮）：账本命中 → 跳编，账本仍 current
        FastPushContext.BeginPush();
        LiveStubInjector.Inject(g, q2);
        FastPushContext.EndPush();
        Assert.True(CompileLedger.IsCurrent(gs, TextHash.Hash(gsQ2)));
    }

    // —— 6. PatchInnerFile（既有按名守卫——回归锚）——

    [Fact]
    public void PatchInnerFile_Twice_NoDuplication()
    {
        var g = Load();
        ModLoader.PatchInnerFile();
        var s1 = SnapshotInner(g);
        ModLoader.PatchInnerFile();
        var s2 = SnapshotInner(g);
        Assert.Equal(s1, s2);
    }

    static (int InnerFns, int ScriptEngines, int ExtFiles, int StartInstances) SnapshotInner(UndertaleData g) => (
        g.Code.Count(c => c.Name.Content is "msl_print" or "give" or "SendMsg" or "createHookObj"
            or "ScriptEngine_server" or "ScriptEngine_create"),
        g.GameObjects.Count(o => o.Name.Content == "o_ScriptEngine"),
        g.Extensions.First(x => x.Name.Content == "display_mouse_lock").Files.Count,
        g.Rooms.First(r => r.Name.Content == "START").GameObjects.Count(go => go.ObjectDefinition?.Name?.Content == "o_ScriptEngine"));

    // —— 7. LoadWeapon（PatchMods 内部增殖点：计划判「仅可代码审读」，实可直接调）——

    class TwinBladesOfTesting : Weapon
    {
        public override void SetDefaults()
        {
            Name = "msl_test_weapon";
            ID = "msl_test_weapon";
            MaxDuration = 100;
        }
    }

    [Fact]
    public void LoadWeapon_Twice_NoDuplication()
    {
        Load();   // vanilla → GetTable 冷路径供静态表
        ModLoader.Initalize();

        ModLoader.LoadWeapon(typeof(TwinBladesOfTesting));
        var w1 = ModLoader.Weapons.Count(x => x.StartsWith("msl_test_weapon"));
        var d1 = ModLoader.WeaponDescriptions.Count(x => x.StartsWith("msl_test_weapon"));

        ModLoader.LoadWeapon(typeof(TwinBladesOfTesting));   // 现状：四路 Insert 各再插一行
        var w2 = ModLoader.Weapons.Count(x => x.StartsWith("msl_test_weapon"));
        var d2 = ModLoader.WeaponDescriptions.Count(x => x.StartsWith("msl_test_weapon"));

        Assert.Equal((1, 3), (w1, d1));   // 首插各表恰好一份（Weapons 1 行 + WeaponDescriptions 3 行）
        Assert.Equal((w1, d1), (w2, d2));   // 重放零增殖
    }
}
