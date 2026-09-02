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

        EnsureFunction(data, ApplyFn, padLocal: false);
        EnsureFunction(data, ReportFn, padLocal: false);
        EnsureFunction(data, LoaderSlot, padLocal: true);
        for (int i = 0; i < quotas.ScriptSlots; i++)
            EnsureFunction(data, $"msl_slot_{i}", padLocal: true);

        // loader 路径拼接用的唯二字符串字面量（loader GML 只允许 boot 字符串，Task 5）
        EnsureString(data, "mods/_live/res/");
        EnsureString(data, ".png");

        var manager = EnsureManager(data);
        EnsureEventContent(manager, EventType.Step, 0, StepEventGml);
        EnsureEventContent(manager, EventType.Other, (uint)EventSubtypeOther.GameStart,
            GameStartGml(quotas.BlankSprites, quotas.BlankPaths));
        EnsureManagerInstance(data, manager);

        for (int i = 0; i < quotas.ShellObjects; i++)
        {
            string parent = quotas.ShellParents[i % quotas.ShellParents.Length];
            var shell = EnsureShell(data, $"o_msl_shell_{i}", parent);
            // 垫 2 var 余量（boot LocalsCount=3）：payload=product 事件代码原样，可有 ≤2 局部
            foreach (var (type, sub) in ShellEventMenu)
                EnsureEvent(shell, type, sub, "var _x = 0;\nvar _y = 0;\nreturn 0;");
        }

        for (int i = 0; i < quotas.EmptyRooms; i++)
            EnsureEmptyRoom(data, $"r_msl_empty_{i}");
        var first = data.Rooms.FirstOrDefault(r => r.Name.Content == "r_msl_empty_0");
        if (first != null) quotas.RoomBaseIndex = data.Rooms.IndexOf(first);

        Log.Information("[live] stub injected: {slots} slots, {shells} shells, {rooms} rooms",
            quotas.ScriptSlots, quotas.ShellObjects, quotas.EmptyRooms);
    }

    static void EnsureFunction(UndertaleData data, string name, bool padLocal)
    {
        var code = data.Code.FirstOrDefault(x => x.Name.Content == name);
        // 垫片矩阵（#16b，LocalsCount 语义 = 编译器「+1 for arguments」公式，子条目取
        // patch.LocalsCount=distinct）：loader/slot 垫 var _t（子=1：loader 载荷 _t 精确
        // 匹配、shell-config-only 0≤1）；apply/report 不垫（trampoline 0 局部 = 0≤0 精确）。
        string body = padLocal ? $"var _t = 0;\nreturn 0;" : "return 0;";
        if (code == null)
        {
            // TheWitcher 形状（fix-loop #16 根因修复，形状真源 = 真机可用的 TW 产物）：
            // function 声明走编译器 isNewFunc 路径，自动生成 gml_Script_ 子条目（Offset=4，
            // 紧跟根）+ prefixed SCPT→子 + prefixed Functions + 裸名 VARI + 根内绑定尾——
            // 运行时 call fn='gml_Script_X'（Functions 名直呼）可解析。裸语句（"return 0;"）
            // 不走该路径，五索引残缺 → "call to non-existent script"（真机 #16 实测）。
            // 不注册裸名 SCPT / GlobalInit：TW 的脚本根不在 GlobalInit。
            Msl.AddFunction($"function {name}() {{\n{body}\n}}", name);
            return;
        }
        // 自愈（34555ad 及更早的历史产物）：裸根无子 → 重编译为 function 声明形态。
        if (data.Code.All(x => x.Name.Content != "gml_Script_" + name))
            code.ReplaceGML($"function {name}() {{\n{body}\n}}", ModLoader.Data);
        // 已是新形态 → 幂等无事。
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
        // 空 creation code：新房间的 SwapCode 目标（spec §6.4 房间行）。
        // 垫 2 var 余量（boot LocalsCount=3）：payload=product 房间 CC 代码原样，可有 ≤2 局部
        var cc = Msl.AddCode("var _x = 0;\nvar _y = 0;\nreturn 0;", $"gml_RoomCC_{name}_0");
        room.CreationCodeId = cc;
    }

    static void EnsureEvent(UndertaleGameObject obj,
        EventType type, uint sub, string gml)
    {
        if (obj.Events[(int)type].Any(e => e.EventSubtype == sub)) return;
        Msl.AddNewEvent(obj, gml, type, sub);
    }

    /// <summary>manager 事件 = 内容寻址（Step/GameStart 承载运行语义，与 shell 静态垫片不同）：
    /// 存在即按当前常量原地重编译——EnsureEvent 的存在性幂等会让增量 patch 流上的历史产物
    /// 永远留住旧 GML（#19 探针这类修订将落不了盘）。结构残缺（无 action/code）→ 摘除重建。</summary>
    static void EnsureEventContent(UndertaleGameObject obj, EventType type, uint sub, string gml)
    {
        var ev = obj.Events[(int)type].FirstOrDefault(e => e.EventSubtype == sub);
        if (ev == null) { Msl.AddNewEvent(obj, gml, type, sub); return; }
        var code = ev.Actions.FirstOrDefault()?.CodeId;
        if (code == null)
        {
            obj.Events[(int)type].Remove(ev);
            Msl.AddNewEvent(obj, gml, type, sub);
            return;
        }
        code.ReplaceGML(gml, ModLoader.Data);
    }

    static void EnsureManagerInstance(UndertaleData data, UndertaleGameObject manager)
    {
        var start = data.Rooms.First(t => t.Name.Content == "START");
        // #18 实证（roomprobe vanilla 对照 + TW 交叉）：GMS2.3 运行时只从 Layer 侧创建
        // 房间实例——vanilla START 两实例两侧镜像（两侧都读会双生），TW initializer 经
        // AddGameObject 双侧写入故多年可用。旧代码只写 legacy GameObjects（与 ModLoader
        // o_ScriptEngine 同款 bug）→ o_msl_live 从未生成 → GameStart/Step 从未跑 →
        // blank 分配/上报/校准全链死（真机 AcquireBlanks 5×-1；「report calibrated」
        // agent.log 全历史零次）。修法 = TW 同款 AddGameObject 双侧写入；幂等以 Layer
        // 侧为准，历史 legacy-only 残留先摘除避免重复。
        var layer = start.GetLayer(UndertaleRoom.LayerType.Instances, "Instances");
        if (layer.InstancesData.Instances.Any(g => g.ObjectDefinition?.Name?.Content == ManagerObject)) return;
        for (int i = start.GameObjects.Count - 1; i >= 0; i--)
            if (start.GameObjects[i].ObjectDefinition?.Name?.Content == ManagerObject)
                start.GameObjects.RemoveAt(i);
        start.AddGameObject("Instances", manager);
    }

    /// <summary>Step 事件 = apply 轮询 + blank 上报。上报放 Step 而不是 GameStart：
    /// trampoline 在 pipe 握手时才安装（Task 14），GameStart 一定早于握手，那里的调用
    /// 只会命中 dummy；Step 每帧轮询保证握手后下一帧即上报，native 侧首次收到后忽略后续。
    /// 上报参数打包：$4D000000 | (spriteFirst &lt;&lt; 8) | pathFirst——高字节固定 tag $4D
    /// 供 agent 做 RValue 偏移自校准（Task 13），spriteFirst &lt; 65536、pathFirst &lt; 256 无碰撞。
    /// #19 诊断前缀（临时，#19 闭环后移除）：首帧写 msl_probe_step.txt 进 save area——
    /// 零新增局部（句柄走 global 不走 var，LocalsCount=1 精确容量钉版不动），文件存在
    /// ⟹ Step 在跑（GameStart 探针已证 GameStart 跑 ≠ Step 跑，C1/C2/C3 三分支判别件）。</summary>
    public const string StepEventGml =
        "if (!variable_global_exists(\"msl_probe_step\"))\n" +
        "{\n" +
        "    global.msl_probe_step = 1;\n" +
        "    global.msl_probe_h = file_text_open_write(\"msl_probe_step.txt\");\n" +
        "    file_text_write_string(global.msl_probe_h, \"step alive spr=\" + string(global.msl_blank_spr_first) + \" path=\" + string(global.msl_blank_path_first));\n" +
        "    file_text_close(global.msl_probe_h);\n" +
        "}\n" +
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
        "}\n" +
        GameStartProbeGml;

    /// <summary>#19 诊断探针（临时，#19 闭环后移除）：接在分配逻辑之后——boot 期把
    /// working_directory/program_directory 实际值、两个 first 全局、以及 sprite_add 四种
    /// 路径形态（裸相对 / working_directory 前缀 / program_directory 前缀 / 正斜杠绝对）的
    /// 真实返回值写进 msl_probe_boot.txt（沙箱写恒 save area = %LOCALAPPDATA%\StoneShard，
    /// 外部可读；若未沙箱化则落 exe 目录，两处都查）。判定：文件不存在 = 实例/GameStart
    /// 从未跑；spr_first=-1 = sprite_add 全败（逐形态返回值指出可用形态）；spr_first>=0 而
    /// report 沉默 = report/校准通道断。GML 串面零转义（writeln 换行、纯正斜杠路径），
    /// 避开编译器转义处理的不确定面。探针追加的 _f 局部不违反任何钉版：Other_2 不在
    /// 垫片矩阵内（Step boot=1 精确容量不许动，Other_2 无交换面契约）。</summary>
    const string GameStartProbeGml =
        "var _f = file_text_open_write(\"msl_probe_boot.txt\");\n" +
        "file_text_write_string(_f, \"wd=\" + working_directory);\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"pd=\" + program_directory);\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"spr_first=\" + string(global.msl_blank_spr_first));\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"path_first=\" + string(global.msl_blank_path_first));\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"rel=\" + string(sprite_add(\"mods/_live/res/_blank.png\", 1, false, false, 0, 0)));\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"wdrel=\" + string(sprite_add(working_directory + \"mods/_live/res/_blank.png\", 1, false, false, 0, 0)));\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"pdrel=\" + string(sprite_add(program_directory + \"mods/_live/res/_blank.png\", 1, false, false, 0, 0)));\n" +
        "file_text_writeln(_f);\n" +
        "file_text_write_string(_f, \"absfwd=\" + string(sprite_add(\"E:/SteamLibrary/steamapps/common/Stoneshard/mods/_live/res/_blank.png\", 1, false, false, 0, 0)));\n" +
        "file_text_close(_f);";
}
