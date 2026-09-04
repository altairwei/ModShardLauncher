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
/// 4) scr_e2e_probe 探针脚本（返回常量；热推换体后返回新值——观测断言的目标函数）
/// 5) oBoot Step 观察者：每帧调探针，值变化才写盘（file_text 通道 = probe5 在本 runner+seed
///    组合上的实证观测手段；禁截图/弹窗）
/// 6) LiveStubInjector.Inject 生产 pass 原样——它是被测对象本身，不改一行
/// 编译顺序约束（AddFunction 顺序 = 正确性约束）：探针先注册，观察者后编译。</summary>
public static class TestDataBuilder
{
    public const string ProbeScript = "scr_e2e_probe";
    public const string BootValue = "111";
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

        // 4) 探针脚本（boot 形态：返回常量 111）
        Msl.AddFunction("function " + ProbeScript + "() { return " + BootValue + "; }", ProbeScript);

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
