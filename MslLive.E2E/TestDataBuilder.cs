using System.IO;
using System.Linq;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace MslLive.E2E;

/// <summary>seed.data.win → E2E 沙箱 data.win 的确定性改造（用户批准：可对 data.win 做
/// 正确改造以适应测试需求）。改造面全部是为了可测试性，不改变被测管线：
/// 1) Room1 → START：EnsureManagerInstance 钉死 vanilla 启动房名（语义等价——seed 只有一间房）
/// 2) GEN8 Name → msl_e2e：GML 相对路径 file_text_* 落 %LOCALAPPDATA%\&lt;GEN8名&gt;\——
///    沙箱观测区与真机 StoneShard 存档区互不污染
/// 3) oBoot Create 的 show_message → 写 boot 标记：原样弹模态框会阻塞游戏线程（无帧可跑）
/// 4) 子探针（vanilla 形状/匿名/长体）+ 聚合探针（boot 就绪信号 "222|200|265"）
/// 5) oBoot Step 观察者：每帧调聚合探针，值变化才写盘（file_text 通道 = probe5 在本 runner+seed
///    组合上的实证观测手段；禁截图/弹窗）
/// 6) oE2ETarget 对象 + START 房 RoomCC（M2/M3/M4 载体）
/// 7) LiveStubInjector.Inject 生产 pass 原样——它是被测对象本身，不改一行
/// 编译顺序约束（AddFunction 顺序 = 正确性约束）：子探针先注册，聚合探针后编译。</summary>
public static class TestDataBuilder
{
    public const string ProbeScript = "scr_e2e_probe";
    public const string ProbeScriptG = "scr_e2e_probe_g";       // vanilla 形状（gml_GlobalScript_ 前缀）
    public const string ProbeScriptA = "scr_e2e_probe_a";       // 匿名函数（AddFunction 扁平化）
    public const string ProbeScriptLong = "scr_e2e_probe_long"; // 15 语句长体
    public const string BootValue = "111";              // boot 就绪信号（聚合探针在 seed 构建时破坏 boot——M1/M10 推送时各自注入探针体）
    public const string ResultFile = "e2e_probe_result.txt";
    public const string BootFile = "e2e_boot.txt";
    public const string SandboxGameName = "msl_e2e";

    public static UndertaleData Build(string seedPath)
    {
        UndertaleData data;
        using (var s = new FileStream(seedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(s, w => { });

        // Msl.* 原语与 LiveStubInjector 的 ReferenceEquals 守卫都绑定 ModLoader.Data（=> DataLoader.data）
        DataLoader.data = data;

        // 1) Room1 → START + 房间速度 0→60：seed 原设计「boot + show_message」从不需要帧
        //    （speed=0 = 帧循环冻结：Create/GameStart 照跑、Step/Draw 永不触发——冒烟首跑
        //    实证：GameStart 探针全绿而 msl_probe_step.txt 缺席）。热加载全程帧驱动。
        var room = data.Rooms.FirstOrDefault(r => r.Name.Content == "Room1")
            ?? throw new InvalidDataException("seed 结构漂移：找不到房间 Room1（fixture 与构建代码不匹配）");
        room.Name = data.Strings.MakeString("START");
        room.Speed = 60;

        // 2) GEN8 游戏名 → msl_e2e（存档区沙箱隔离）
        data.GeneralInfo.Name = data.Strings.MakeString(SandboxGameName);

        // 3) oBoot Create：show_message → 初始化观察者 global + boot 标记写盘（去模态框 + 就绪信号）。
        //    global.e2e_last 必须先初始化：GMS 2.2 runner 读未设 global 直接 Code Error 弹模态框
        //    冻结帧循环（冒烟次跑实证：'e2e_last' index (100006) not set before reading it）。
        var create = data.Code.FirstOrDefault(c => c.Name.Content == "gml_Object_oBoot_Create_0")
            ?? throw new InvalidDataException("seed 结构漂移：找不到 gml_Object_oBoot_Create_0");
        create.ReplaceGML(
            "global.e2e_last = -1;\n" +
            "var _f = file_text_open_write(\"" + BootFile + "\");\n" +
            "file_text_write_string(_f, \"boot\");\n" +
            "file_text_close(_f);", data);

        // 4) 子探针（先注册——聚合探针引用它们）
        //     [二分 D：加回 4b vanilla 形状探针]

        // 4b) vanilla 形状探针（M1）：根条目名 gml_GlobalScript_ 前缀，体用 argument[0] 传参
        Msl.AddFunction(
            "function " + ProbeScriptG + "(_k) { return argument[0] + 111; }",
            ProbeScriptG);
        var gEntry = data.Code.First(c => c.Name.Content == ProbeScriptG);
        gEntry.Name = data.Strings.MakeString("gml_GlobalScript_" + ProbeScriptG);

        // 4c) 匿名函数探针（M6）：AddFunction 扁平化
        //     [二分发现：匿名函数在 seed 构建时破坏 boot——runner 无法启动。
        //      M6 改为推送时动态注入（测试中 AddFunction 编译匿名体，然后推送），
        //      boot 不携带匿名函数。]
        // Msl.AddFunction(
        //     "function " + ProbeScriptA + "() { var _g = function(_x) { return _x * 2; }; return _g(100); }",
        //     ProbeScriptA);

        // 4d) 长体探针（M10）：15 语句，缩短测试的载体
        Msl.AddFunction(
            "function " + ProbeScriptLong + "() {\n" +
            "    var _a = 1; var _b = 2; var _c = 3; var _d = 4; var _e = 5;\n" +
            "    var _f = _a + _b; var _g2 = _c + _d; var _h = _e * 2;\n" +
            "    var _i = _f + _g2; var _j = _h + _i;\n" +
            "    var _k = _j + 10; var _l = _k + 20; var _m = _l + 30;\n" +
            "    var _n = _m + 40; var _o = _n + 50;\n" +
            "    return _o;\n" +
            "}", ProbeScriptLong);

        // 4e) 聚合探针（boot 就绪信号）：三子探针拼接
        //     [二分发现：聚合探针（string() + "|" 字面量 + 多脚本调用）也破坏 boot——
        //      seed 构建时 runner 无法启动。聚合探针改为推送时注入（M1/M10 各自推
        //      自己的探针体，观测通道不变）。boot 就绪信号回到 "111"。]
        Msl.AddFunction(
            "function " + ProbeScript + "() { return 111; }",
            ProbeScript);

        // 4f) oE2ETarget 对象（M2/M3）：Create 初始化 + Step 每帧写 global.e2e_target
        Msl.AddObject("oE2ETarget");
        Msl.AddNewEvent("oE2ETarget",
            "global.e2e_target = 0;", EventType.Create, 0);
        Msl.AddNewEvent("oE2ETarget",
            "global.e2e_target = global.e2e_target + 1;", EventType.Step, 0);
        // 放进 START 房——AddGameObject 双侧写入（#18 实证）
        var room2 = data.Rooms.First(r => r.Name.Content == "START");
        var layer2 = room2.GetLayer(UndertaleRoom.LayerType.Instances, "Instances");
        if (!layer2.InstancesData.Instances.Any(g => g.ObjectDefinition?.Name?.Content == "oE2ETarget"))
            room2.AddGameObject("Instances", data.GameObjects.First(o => o.Name.Content == "oE2ETarget"));

        // 4g) START 房 RoomCC（M4）：空 Code 条目挂 CreationCodeId + ReplaceGML 填充
        var ccEntry = new UndertaleCode { Name = data.Strings.MakeString("gml_RoomCC_START_0") };
        data.Code.Add(ccEntry);
        ccEntry.ReplaceGML("global.e2e_roomcc = 0;", data);
        room2.CreationCodeId = ccEntry;

        // 5) oBoot Step 观察者：值变化才落盘（首帧即写 111——这本身就是链路就绪信号）
        Msl.AddNewEvent("oBoot",
            "var _v = " + ProbeScript + "();\n" +
            "if (_v != global.e2e_last)\n" +
            "{\n" +
            "    global.e2e_last = _v;\n" +
            "    var _f = file_text_open_write(\"" + ResultFile + "\");\n" +
            "    file_text_write_string(_f, string(_v));\n" +
            "    file_text_close(_f);\n" +
            "}", EventType.Step, 0);

        // 6) 生产注入 pass 原样。配额唯 ShellParents 改全空桶：默认前四桶指向 o_button/
        //    o_menuParent（vanilla 对象），seed 里不存在会让 AddObject 的父类查找抛错；
        //    空父桶是生产支持的合法形态（默认后四桶即 ""）。壳父桶分配机制非本流水线
        //    被测面——要测对象分配时再往 seed 补父类对象。
        LiveStubInjector.Inject(data, new LiveQuotas
        {
            ShellParents = new[] { "", "", "", "", "", "", "", "" },
        });
        return data;
    }
}
