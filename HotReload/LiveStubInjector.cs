using System;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;
using Serilog;

namespace ModShardLauncher.HotReload;

/// <summary>Dev 模式静态 pass（spec §4.1 stub + §6.2 槽位池 + §7.3 空白分配）。
/// 编译产物里的全部热加载静态结构；对 mod 作者零暴露；幂等（重跑不重复注入）。
/// 注入位置 = PatchFile 末尾 → 槽位/壳/房间索引排在 mod 内容之后；运行时一律按名解析（NodeIndex），
/// 文件索引不参与运行时定位（Task 13）。
/// 注意：Msl.* 原语硬绑定 ModLoader.Data（既有架构），因此要求传入的 data 就是它——
/// 调用方（PatchFile）天然满足；测试须先把 DataLoader.data 指到测试装载的 data。
/// 不一致 = 存在性检查与变异落在两个对象上，幂等性静默失效——直接拒绝（fail-closed）。</summary>
public static class LiveStubInjector
{
    public const string ManagerObject = "o_msl_live";
    public const string LoaderSlot = "msl_loader_0";
    public const string ApplyFn = "msl_live_apply";
    public const string ReportFn = "msl_live_report";
    public const string ManagerStepEntry = "gml_Object_o_msl_live_Step_0";   // RunGml 触发器宿主（Task 9/15）

    static readonly (EventType Type, uint Sub)[] ShellEventMenu =
    {
        (EventType.Create, 0),
        (EventType.Step, 0), (EventType.Step, 1), (EventType.Step, 2),
        (EventType.Alarm, 0), (EventType.Alarm, 1), (EventType.Alarm, 2), (EventType.Alarm, 3),
        (EventType.Draw, 0), (EventType.Draw, 64),
        (EventType.Other, 10), (EventType.Other, 11), (EventType.Other, 12), (EventType.Other, 13),
        (EventType.Other, 14), (EventType.Other, 15), (EventType.Other, 16),
    };

    public static void Inject(UndertaleData data, LiveQuotas quotas)
    {
        if (!ReferenceEquals(data, ModLoader.Data))
            throw new InvalidOperationException("LiveStubInjector 要求 data == ModLoader.Data（Msl.* 原语的全局绑定）");

        EnsureFunction(data, ApplyFn);
        EnsureFunction(data, ReportFn);
        EnsureFunction(data, LoaderSlot);
        for (int i = 0; i < quotas.ScriptSlots; i++)
            EnsureFunction(data, $"msl_slot_{i}");

        // loader 路径拼接用的唯二字符串字面量（loader GML 只允许 boot 字符串，Task 5）
        EnsureString(data, "mods/_live/res/");
        EnsureString(data, ".png");

        var manager = EnsureManager(data);
        EnsureEvent(manager, EventType.Step, 0, StepEventGml);
        EnsureEvent(manager, EventType.Other, (uint)EventSubtypeOther.GameStart,
            GameStartGml(quotas.BlankSprites, quotas.BlankPaths));
        EnsureManagerInstance(data, manager);

        for (int i = 0; i < quotas.ShellObjects; i++)
        {
            string parent = quotas.ShellParents[i % quotas.ShellParents.Length];
            var shell = EnsureShell(data, $"o_msl_shell_{i}", parent);
            foreach (var (type, sub) in ShellEventMenu)
                EnsureEvent(shell, type, sub, "return 0;");
        }

        for (int i = 0; i < quotas.EmptyRooms; i++)
            EnsureEmptyRoom(data, $"r_msl_empty_{i}");
        var first = data.Rooms.FirstOrDefault(r => r.Name.Content == "r_msl_empty_0");
        if (first != null) quotas.RoomBaseIndex = data.Rooms.IndexOf(first);

        Log.Information("[live] stub injected: {slots} slots, {shells} shells, {rooms} rooms",
            quotas.ScriptSlots, quotas.ShellObjects, quotas.EmptyRooms);
    }

    static void EnsureFunction(UndertaleData data, string name)
    {
        if (data.Code.All(x => x.Name.Content != name))
            Msl.AddFunction("return 0;", name);
    }

    static void EnsureString(UndertaleData data, string content)
    {
        if (data.Strings.All(s => s.Content != content))
            data.Strings.MakeString(content);
    }

    static UndertaleGameObject EnsureManager(UndertaleData data)
    {
        var obj = Msl.AddObject(ManagerObject);
        obj.Persistent = true;
        return obj;
    }

    static UndertaleGameObject EnsureShell(UndertaleData data, string name, string parent)
        => Msl.AddObject(name, spriteName: "", parentName: parent);

    static void EnsureEmptyRoom(UndertaleData data, string name)
    {
        if (data.Rooms.Any(r => r.Name.Content == name)) return;
        var room = Msl.AddRoom(name);
        // 空 creation code：新房间的 SwapCode 目标（spec §6.4 房间行）
        var cc = Msl.AddCode("return 0;", $"gml_RoomCC_{name}_0");
        room.CreationCodeId = cc;
    }

    static void EnsureEvent(UndertaleGameObject obj,
        EventType type, uint sub, string gml)
    {
        if (obj.Events[(int)type].Any(e => e.EventSubtype == sub)) return;
        Msl.AddNewEvent(obj, gml, type, sub);
    }

    static void EnsureManagerInstance(UndertaleData data, UndertaleGameObject manager)
    {
        var start = data.Rooms.First(t => t.Name.Content == "START");
        if (start.GameObjects.Any(g => g.ObjectDefinition?.Name?.Content == ManagerObject)) return;
        start.GameObjects.Add(new UndertaleRoom.GameObject
        {
            ObjectDefinition = manager,
            InstanceID = data.GeneralInfo.LastObj++,
        });
    }

    /// <summary>Step 事件 = apply 轮询 + blank 上报。上报放 Step 而不是 GameStart：
    /// trampoline 在 pipe 握手时才安装（Task 14），GameStart 一定早于握手，那里的调用
    /// 只会命中 dummy；Step 每帧轮询保证握手后下一帧即上报，native 侧首次收到后忽略后续。
    /// 上报参数打包：$4D000000 | (spriteFirst &lt;&lt; 8) | pathFirst——高字节固定 tag $4D
    /// 供 agent 做 RValue 偏移自校准（Task 13），spriteFirst &lt; 65536、pathFirst &lt; 256 无碰撞。</summary>
    public const string StepEventGml =
        "msl_live_apply();\n" +
        "if (global.msl_blank_spr_first >= 0 && global.msl_blank_path_first >= 0)\n" +
        "    msl_live_report($4D000000 | (global.msl_blank_spr_first << 8) | global.msl_blank_path_first);";

    static string GameStartGml(int blankSprites, int blankPaths) =>
        "global.msl_blank_spr_first = -1;\n" +
        "global.msl_blank_spr_count = 0;\n" +
        $"repeat ({blankSprites})\n" +
        "{\n" +
        "    var _s = sprite_add(\"mods/_live/res/_blank.png\", 1, false, false, 0, 0);\n" +
        "    if (global.msl_blank_spr_first < 0) global.msl_blank_spr_first = _s;\n" +
        "    global.msl_blank_spr_count += 1;\n" +
        "}\n" +
        "global.msl_blank_path_first = -1;\n" +
        "global.msl_blank_path_count = 0;\n" +
        $"repeat ({blankPaths})\n" +
        "{\n" +
        "    var _p = path_add();\n" +
        "    if (global.msl_blank_path_first < 0) global.msl_blank_path_first = _p;\n" +
        "    global.msl_blank_path_count += 1;\n" +
        "}";
}
