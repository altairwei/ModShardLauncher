using System.Text;
using ModShardLauncher;
using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using Xunit;
using Xunit.Abstractions;

namespace MslLive.E2E;

/// <summary>hot-reload 分支的 E2E 流水线：真 runner（游戏钉版同构 VM）+ 真 agent（version.dll
/// 代理注入）+ 真管道 + 真两阶段应用 + 真 VM 执行断言——替代人工「开游戏看效果」的第三层验证。
/// 单类串行（xunit 类内顺序执行；MSL 客户端静态是进程级的）。</summary>
[Collection("E2E-Serial")]
public class E2ETests : IDisposable
{
    readonly LiveHarness h = new();
    readonly ITestOutputHelper output;

    public E2ETests(ITestOutputHelper output) => this.output = output;

    /// <summary>沙箱数据清单契约（纯离线，不拉 runner）：TestDataBuilder + LiveStubInjector
    /// 产出的条目形状守卫——结构漂移（fixture/构建代码/注入器不匹配）时先于 E2E 主测试红，
    /// 同时输出完整清单（Code/SCPT/FUNC/STRG）作为「数据侧名字 vs 运行时索引名」对照的
    /// 第一手证据（proof 取证用）。</summary>
    [Fact]
    public void SandboxManifest_MatchesBuilderContract()
    {
        var data = TestDataBuilder.Build(E2EFixture.SeedWin);
        var sb = new StringBuilder();
        sb.AppendLine("== Code entries ==");
        foreach (var c in data.Code)
            sb.AppendLine($"  {c.Name.Content}  locals={c.LocalsCount}  parent={(c.ParentEntry?.Name.Content ?? "<null>")}");
        sb.AppendLine("== Scripts (SCPT) ==");
        foreach (var s in data.Scripts)
            sb.AppendLine($"  {s.Name.Content} -> code '{s.Code?.Name.Content}'");
        sb.AppendLine("== Functions (FUNC) ==");
        int fi = 0;
        foreach (var f in data.Functions)
            sb.AppendLine($"  [{fi++}] {f.Name.Content}");
        sb.AppendLine("== Strings (STRG) ==");
        int si = 0;
        foreach (var s in data.Strings)
            sb.AppendLine($"  [{si++}] {s.Content}");
        // 观察者 + 探针 root/child 反汇编：proof「encoded != live @0x4」取证面
        // （首条指令的操作数到底是函数引用/变量 id/字符串索引）
        foreach (string name in new[] { "gml_Object_oBoot_Step_0", TestDataBuilder.ProbeScript,
                                        "gml_Script_" + TestDataBuilder.ProbeScript })
        {
            var c = data.Code.FirstOrDefault(x => x.Name.Content == name);
            sb.AppendLine($"== Disasm: {name} ==");
            if (c != null)
                sb.AppendLine(c.Disassemble(data.Variables, data.CodeLocals.For(c)));
            else sb.AppendLine("  <missing>");
        }
        output.WriteLine(sb.ToString());
        // 完整清单同时落盘（控制台会截断——对账要全量）
        File.WriteAllText(System.IO.Path.Combine(E2EFixture.RepoRoot, "E2EFixture", "manifest.txt"), sb.ToString());

        // 最小契约：探针/观察者/注入器核心条目必须在位（漂移即红，带全清单上下文）
        Assert.Contains(data.Code, c => c.Name.Content == "gml_Object_oBoot_Create_0");
        Assert.Contains(data.Code, c => c.Name.Content == TestDataBuilder.ProbeScript);
        Assert.Contains(data.Code, c => c.Name.Content == "gml_Object_oBoot_Step_0");
        Assert.Contains(data.Code, c => c.Name.Content == "gml_Object_o_msl_live_Step_0");
        // 矩阵增强资产（Task #75）——匿名探针（ProbeScriptA）不在 seed（boot 破坏 runner，
        // M6 推送时动态注入）；聚合探针回退到简单探针（boot 破坏 runner，M1/M10 推送时注入）
        Assert.Contains(data.Code, c => c.Name.Content == "gml_GlobalScript_" + TestDataBuilder.ProbeScriptG);
        Assert.Contains(data.Code, c => c.Name.Content == TestDataBuilder.ProbeScriptLong);
        Assert.Contains(data.GameObjects, o => o.Name.Content == "oE2ETarget");
        Assert.Contains(data.Code, c => c.Name.Content == "gml_Object_oE2ETarget_Create_0");
        Assert.Contains(data.Code, c => c.Name.Content == "gml_Object_oE2ETarget_Step_0");
        Assert.Contains(data.Code, c => c.Name.Content == "gml_RoomCC_START_0");
        var startRoom = data.Rooms.First(r => r.Name.Content == "START");
        Assert.NotNull(startRoom.CreationCodeId);
        Assert.Equal("gml_RoomCC_START_0", startRoom.CreationCodeId!.Name.Content);
    }

    /// <summary>冒烟（E2E-D）：握手全链（hello/vars/proof/blanks）+ 平凡 swap 生效断言。
    /// 覆盖生产主路径：既有脚本函数体编辑 → diff → 编码 → 推送 → agent 应用 → 下一帧 VM 执行新体。</summary>
    [E2EFact]
    public void Smoke_TrivialSwap_HotAppliedAndExecuted()
    {
        h.Boot("smoke");

        var r = h.PushProbeBody("function scr_e2e_probe() { return 222; }");

        Assert.True(r.Attempted, "热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        string? got = h.WaitResult("222");
        Assert.True(got == "222",
            "VM 未执行换入体（观测=" + got + "，期望 222）\n" + h.Diagnostics());
    }

    /// <summary>fix #32 全链路验收（E2E-E）：数组局部 var 经旧编译器错发射（元素访问
    /// Undefined(0) 域）→ MSL 救援层翻 -7 + 借位 id（#30）→ agent 应用 → VM 真数组语义执行。
    /// 回报值 42 = 数组存储 + 元素写 + 元素读 + 算术全对；任何一环错都会变成别的值或崩溃。</summary>
    [E2EFact]
    public void ArrayLocal_Fix32_RescueBorrowAndExecute()
    {
        h.Boot("array_local");

        var r = h.PushProbeBody(
            "function scr_e2e_probe() {\n" +
            "    var _arr = [1, 2, 3];\n" +
            "    _arr[0] = 40;\n" +
            "    _arr[1] = _arr[0] + 2;\n" +
            "    return _arr[1];\n" +
            "}");

        Assert.True(r.Attempted, "热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        string? got = h.WaitResult("42");
        Assert.True(got == "42",
            "数组局部热语义断裂（观测=" + got + "，期望 42）\n" + h.Diagnostics());
    }

    /// <summary>fix #34 全链路验收（E2E-F）：boot STRG 没有的新字符串字面量（含多字节
    /// UTF-8 中文）→ StrgIndex=-1 → agent StrgAppendix（游戏堆新块 + 新偏移表 + commit 窗
    /// RCU 换槽）→ VM push.s 现查表解析新 id → RValue strcpy → return → 观察者落盘。
    /// 观测 == 原字面量 = 追加/换槽/解析/构造全链对；旧语义这里整批拒（non-boot string）。</summary>
    [E2EFact]
    public void StrgAppend_FreshString_HotAppliedAndExecuted()
    {
        h.Boot("strg_append");

        const string fresh = "fix34_崭新字符串";
        var r = h.PushProbeBody("function scr_e2e_probe() { return \"" + fresh + "\"; }");

        Assert.True(r.Attempted, "热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        string? got = h.WaitResult(fresh);
        Assert.True(got == fresh,
            "新字符串热语义断裂（观测=" + got + "，期望 " + fresh + "）\n" + h.Diagnostics());
    }

    /// <summary>fix #35 验收（E2E-H，真机 14:10 场景的沙箱复现）：#33 激活门 patch 只写
    /// 活内存——同会话重推同条目时，镜像检查/patch 资格若读 boot 快照则「locals mismatch」
    /// 整批拒。两次推送同探针（均带 var 局部 = patch 载体）：第一次 patch 落地、第二次必须
    /// 依然过（活体镜像 1==1）且幂等（已开的门不重写）。观测先 7 后 9 = 两代换入体都真执行。</summary>
    [E2EFact]
    public void SameEntry_RepushAfterPatch_StillApplies()
    {
        h.Boot("repush");

        var r1 = h.PushProbeBody("function scr_e2e_probe() { var _x = 7; return _x; }");
        Assert.True(r1.Attempted && r1.Succeeded,
            "首推失败：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("7") == "7", "首推观测≠7\n" + h.Diagnostics());

        var r2 = h.PushProbeBody("function scr_e2e_probe() { var _y = 9; return _y; }");
        Assert.True(r2.Attempted, "重推未启动：" + string.Join("；", r2.Failures) + "\n" + h.Diagnostics());
        Assert.True(r2.Succeeded,
            "重推失败（14:10 形态：locals mismatch 误拒）：" + string.Join("；", r2.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("9") == "9", "重推观测≠9\n" + h.Diagnostics());
    }

    /// <summary>fix #36-B 验收（E2E-I，真机 16:06 场景的沙箱复现）：新实例变量（boot 无
    /// VARI 来源）的引用经 MSL 救援改写为动态 API（variable_instance_get/set 按名字走
    /// 运行时符号注册 + map 存储）——agent 零改动、对 boot 前旧实例天然有效。观测 42 =
    /// 写（set 经中转局部）+ 读（get）+ 算术全对；旧语义这里整批拒（共享容器红线）。</summary>
    [E2EFact]
    public void NewInstanceVar_RescuedViaDynamicApi()
    {
        h.Boot("fresh_ivar");

        var r = h.PushProbeBody(
            "function scr_e2e_probe() { fresh_ivar_val = 41;\nreturn fresh_ivar_val + 1; }");

        Assert.True(r.Attempted, "热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("42") == "42",
            "新实例变量热语义断裂（期望 42）\n" + h.Diagnostics());
    }

    /// <summary>fix #36-B 对照实验（逐指令二分）：手写产物形态正确（Push String + Conv s.v +
    /// Call——与 boot 观察者一致）但运行时断 → 问题在编码字节层。三轮逐层：
    /// ① 数字参数内置调用（abs）；② 变量传字符串（string_length(变量)）；③ 字面量传串（已知断）。</summary>
    [E2EFact]
    public void DynamicApi_LayeredControl()
    {
        h.Boot("dyn_api_ctl");

        var r1 = h.PushProbeBody("function scr_e2e_probe() { return abs(-4); }");
        Assert.True(r1.Succeeded, "① 推送失败：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("4") == "4",
            "① 数字参数内置调用断裂（期望 4）\n" + h.Diagnostics());

        var r2 = h.PushProbeBody(
            "function scr_e2e_probe() { var _s = \"boot\";\nreturn string_length(_s); }");
        Assert.True(r2.Succeeded, "② 推送失败：" + string.Join("；", r2.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("4") == "4",
            "② 变量传字符串断裂（期望 4）\n" + h.Diagnostics());

        var r3 = h.PushProbeBody("function scr_e2e_probe() { return string_length(\"boot\"); }");
        Assert.True(r3.Succeeded, "③ 推送失败：" + string.Join("；", r3.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("4") == "4",
            "③ 字面量传字符串断裂（期望 4）\n" + h.Diagnostics());
    }

    /// <summary>「mod 升级」完整形态验收（E2E-G，真机 09:53 场景的沙箱复现）：同批 =
    /// 新脚本（product-only → msl_slot_N 槽热加）+ 既有探针改体（调新脚本）+ 新字符串。
    /// 真机那批 39 entries 整批被 getseed 拒——16 个槽 op 从未 commit 过，槽路径的
    /// apply 从未在真 VM 执行（此前只有单测）；本测试一石三鸟：槽换入 + 跨槽调用
    /// （call.i → 槽名操作数解析）+ StrgAppendix 同批。观测 = 新脚本 return 的新字符串。</summary>
    [E2EFact]
    public void NewScriptViaSlot_CalledFromProbe_WithFreshString()
    {
        h.Boot("slot_fresh");

        const string fresh = "fix34g_槽上新生";
        var r = h.PushProbeWithNewScript(
            newScriptName: "scr_e2e_newcmd",
            newScriptBody: "function scr_e2e_newcmd() { return \"" + fresh + "\"; }",
            probeBody: "function scr_e2e_probe() { return scr_e2e_newcmd(); }");

        Assert.True(r.Attempted, "热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        string? got = h.WaitResult(fresh);
        Assert.True(got == fresh,
            "新脚本槽热语义断裂（观测=" + got + "，期望 " + fresh + "）\n" + h.Diagnostics());
    }

    // ========== 场景矩阵清扫（Task #77，2026-09-06） ==========
    // 设计文档：docs/superpowers/plans/2026-09-06-e2e-scenario-matrix.md
    // 判定标准：绿 = Attempted && Succeeded + 观测值对 + runner 存活
    //          真 bug = 崩溃/错值/静默错行为
    //          诚实边界 = 干净拒批 + 拒批后管道健康（AssertPipelineHealthyAfterRejection）

    /// <summary>M1：vanilla 形状脚本编辑（gml_GlobalScript_ 前缀根条目 + argument[0] 传参）。
    /// 真游戏每次 vanilla 脚本编辑的形态——#22（前缀剥离）已修但 E2E 首走。
    /// 观测：推送后探针改调 probe_g，返回值变化证明 vanilla 形状条目热换成功。</summary>
    [E2EFact]
    public void M1_VanillaShapeScript_EditAndHotSwap()
    {
        h.Boot("m1_vanilla");

        // 编辑 gml_GlobalScript_scr_e2e_probe_g 的体（vanilla 形状：argument[0] 传参）
        var r1 = h.PushProduct(product =>
        {
            var code = product.Code.First(c => c.Name.Content == "gml_GlobalScript_" + TestDataBuilder.ProbeScriptG);
            code.ReplaceGML(
                "function " + TestDataBuilder.ProbeScriptG + "(_k) { return argument[0] + 999; }",
                product);
        });
        Assert.True(r1.Attempted, "M1 热通道未启动：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());
        Assert.True(r1.Succeeded, "M1 vanilla 形状条目热推失败：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());

        // 观测：改探针体调 probe_g——返回值 = 111 + 999 = 1110
        var r2 = h.PushProbeBody("function scr_e2e_probe() { return scr_e2e_probe_g(111); }");
        Assert.True(r2.Succeeded, "M1 观测探针推送失败：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("1110") == "1110",
            "M1 vanilla 形状热语义断裂（期望 1110）\n" + h.Diagnostics());
    }

    /// <summary>M2：对象 Step 事件编辑。oE2ETarget Step 每帧 global.e2e_target += 1 →
    /// 编辑为 += 10。观测 = 推送成功 + runner 存活（运行时行为变化需 global 中继，
    /// 超出 v1 范围——只验证事件条目热换不崩）。</summary>
    [E2EFact]
    public void M2_ObjectStepEvent_EditAndHotSwap()
    {
        h.Boot("m2_objstep");

        var r = h.EditObjectEvent("oE2ETarget", EventType.Step, 0,
            "global.e2e_target = global.e2e_target + 10;");
        Assert.True(r.Attempted, "M2 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M2 对象 Step 事件热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());

        // runner 存活验证：再推一个平凡编辑仍成功
        var r2 = h.PushProbeBody("function scr_e2e_probe() { return 555; }");
        Assert.True(r2.Succeeded, "M2 推送后管道毒化：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("555") == "555", "M2 推送后观测通道死\n" + h.Diagnostics());
    }

    /// <summary>M3：对象 Create 事件编辑。oE2ETarget Create 初始化 global.e2e_target = 0 →
    /// 编辑为 = 100。观测 = 推送成功 + runner 存活（新实例重跑 Create 的行为观测
    /// 需 instance_create 触发，超出 v1 范围）。</summary>
    [E2EFact]
    public void M3_ObjectCreateEvent_EditAndHotSwap()
    {
        h.Boot("m3_objcreate");

        var r = h.EditObjectEvent("oE2ETarget", EventType.Create, 0,
            "global.e2e_target = 100;");
        Assert.True(r.Attempted, "M3 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M3 对象 Create 事件热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());

        var r2 = h.PushProbeBody("function scr_e2e_probe() { return 555; }");
        Assert.True(r2.Succeeded, "M3 推送后管道毒化：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("555") == "555", "M3 推送后观测通道死\n" + h.Diagnostics());
    }

    /// <summary>M4：RoomCC 编辑。START 房 CC 体 global.e2e_roomcc = 0 → 编辑为 = 42。
    /// 观测 = 推送成功 + runner 存活（room_restart 重入的行为观测超出 v1 范围）。
    /// 高风险格：RoomCC 是 boot 时执行的代码，热换后不重跑——只验证热换不崩。</summary>
    [E2EFact]
    public void M4_RoomCC_EditAndHotSwap()
    {
        h.Boot("m4_roomcc");

        var r = h.EditRoomCC("START", "global.e2e_roomcc = 42;");
        Assert.True(r.Attempted, "M4 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M4 RoomCC 热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());

        var r2 = h.PushProbeBody("function scr_e2e_probe() { return 555; }");
        Assert.True(r2.Succeeded, "M4 推送后管道毒化：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("555") == "555", "M4 推送后观测通道死\n" + h.Diagnostics());
    }

    /// <summary>M5：新全局变量（global.e2e_fresh = 5; return global.e2e_fresh;）。
    /// 预测今日拒批——#36-B 只救 Self 域（实例变量），Global 域是另一条路径。
    /// 若拒批 → 诚实边界（#37 候选：variable_global_set/get 救援）。</summary>
    [E2EFact]
    public void M5_FreshGlobalVariable()
    {
        h.Boot("m5_global");

        var r = h.PushProbeBody(
            "function scr_e2e_probe() { global.e2e_fresh = 5;\nreturn global.e2e_fresh; }");

        // 理想断言：绿（推送成功 + 观测 5）
        Assert.True(r.Attempted, "M5 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M5 新全局变量热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("5") == "5",
            "M5 新全局变量热语义断裂（期望 5）\n" + h.Diagnostics());
    }

    /// <summary>M6：mod 侧匿名函数热推（AddFunction 扁平化——匿名体内联进根条目，
    /// 子条目只是 FUNC 锚点）。推送时动态注入：AddFunction 编译匿名体 → 推送。
    /// 观测 = 匿名函数返回值（fnref + method() 调用面热换成功）。</summary>
    [E2EFact]
    public void M6_AnonymousFunction_HotPush()
    {
        h.Boot("m6_anon");

        var r = h.PushProduct(product =>
        {
            Msl.AddFunction(
                "function scr_e2e_probe_anon() { var _g = function(_x) { return _x * 3; }; return _g(100); }",
                "scr_e2e_probe_anon");
            var code = product.Code.First(c => c.Name.Content == TestDataBuilder.ProbeScript);
            code.ReplaceGML("function scr_e2e_probe() { return scr_e2e_probe_anon(); }", product);
        });

        Assert.True(r.Attempted, "M6 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M6 匿名函数热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("300") == "300",
            "M6 匿名函数热语义断裂（期望 300）\n" + h.Diagnostics());
    }

    /// <summary>M7：编辑中新增匿名函数（product-only 新 Code 子条目）。
    /// 探针体加匿名函数——匿名子条目是 product-only 新条目，预测拒批（槽外新条目）。</summary>
    [E2EFact]
    public void M7_NewAnonymousInEdit()
    {
        h.Boot("m7_newanon");

        var r = h.PushProbeBody(
            "function scr_e2e_probe() { var _f = function(_x) { return _x + 1; }; return _f(41); }");

        // 理想断言：绿（推送成功 + 观测 42）
        Assert.True(r.Attempted, "M7 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M7 新增匿名函数热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("42") == "42",
            "M7 新增匿名函数热语义断裂（期望 42）\n" + h.Diagnostics());
    }

    /// <summary>M8：method() 调用链（method(self, helper) 后调用）。
    /// #36-B 附带修复（OverlayFunctions 误槽化）的 E2E 化。</summary>
    [E2EFact]
    public void M8_MethodCallChain()
    {
        h.Boot("m8_method");

        var r = h.PushProbeWithNewScript(
            newScriptName: "scr_e2e_helper",
            newScriptBody: "function scr_e2e_helper() { return 77; }",
            probeBody: "function scr_e2e_probe() { var _m = method(self, scr_e2e_helper); return _m(); }");

        Assert.True(r.Attempted, "M8 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M8 method() 热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("77") == "77",
            "M8 method() 热语义断裂（期望 77）\n" + h.Diagnostics());
    }

    /// <summary>M9：控制流混合 + fresh builtin（for/switch/with/repeat + sqrt——
    /// seed FUNC 表没有 sqrt，测 agent FUNC 解析器路径）。</summary>
    [E2EFact]
    public void M9_ControlFlow_FreshBuiltin()
    {
        h.Boot("m9_ctrlflow");

        var r = h.PushProbeBody(
            "function scr_e2e_probe() {\n" +
            "    var _sum = 0;\n" +
            "    for (var _i = 0; _i < 5; _i++) { _sum += _i; }\n" +
            "    switch (_sum) { case 10: _sum += 100; break; default: _sum = -1; break; }\n" +
            "    repeat (3) { _sum += 1; }\n" +
            "    var _r = sqrt(16);\n" +
            "    return _sum + _r;\n" +
            "}");

        Assert.True(r.Attempted, "M9 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M9 控制流+builtin 热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        // _sum = 0+1+2+3+4=10 → switch +100 → 110 → repeat +3 → 113 → sqrt(16)=4 → 117
        Assert.True(h.WaitResult("117") == "117",
            "M9 控制流+builtin 热语义断裂（期望 117）\n" + h.Diagnostics());
    }

    /// <summary>M10：条目缩短（15 语句长体 → 一行）。
    /// 编辑 scr_e2e_probe_long 从 15 语句到 return 42。</summary>
    [E2EFact]
    public void M10_ShrinkEntry()
    {
        h.Boot("m10_shrink");

        var r1 = h.PushProduct(product =>
        {
            var code = product.Code.First(c => c.Name.Content == TestDataBuilder.ProbeScriptLong);
            code.ReplaceGML("function " + TestDataBuilder.ProbeScriptLong + "() { return 42; }", product);
        });
        Assert.True(r1.Attempted, "M10 热通道未启动：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());
        Assert.True(r1.Succeeded, "M10 条目缩短热推失败：" + string.Join("；", r1.Failures) + "\n" + h.Diagnostics());

        // 观测：改探针体调 probe_long
        var r2 = h.PushProbeBody("function scr_e2e_probe() { return scr_e2e_probe_long(); }");
        Assert.True(r2.Succeeded, "M10 观测探针推送失败：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("42") == "42",
            "M10 条目缩短热语义断裂（期望 42）\n" + h.Diagnostics());
    }

    /// <summary>M11：条目删除（product 删掉 boot 已有脚本 probe_long）。
    /// 预测 no-op：热推送不处理删除（条目在 VM 里仍在），重启后生效。
    /// 观测 = 推送成功（0 变更或干净拒批）+ 管道健康。</summary>
    [E2EFact]
    public void M11_DeleteEntry()
    {
        h.Boot("m11_delete");

        var r = h.DeleteScript(TestDataBuilder.ProbeScriptLong);
        // 理想断言：绿（推送成功——删除被识别为 0 变更或干净拒批）
        Assert.True(r.Attempted, "M11 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M11 条目删除热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());

        // 管道健康：再推平凡编辑仍成功
        var r2 = h.PushProbeBody("function scr_e2e_probe() { return 888; }");
        Assert.True(r2.Succeeded, "M11 删除后管道毒化：" + string.Join("；", r2.Failures));
        Assert.True(h.WaitResult("888") == "888", "M11 删除后观测通道死\n" + h.Diagnostics());
    }

    /// <summary>M12：unchanged 重推（同体再推，0 变更）。
    /// AppliedEntries=0 的成功语义要钉清——不是错误，不是 no-op，是合法的空推送。</summary>
    [E2EFact]
    public void M12_UnchangedRepush()
    {
        h.Boot("m12_unchanged");

        // 首推：改体到 333
        var r1 = h.PushProbeBody("function scr_e2e_probe() { return 333; }");
        Assert.True(r1.Succeeded, "M12 首推失败：" + string.Join("；", r1.Failures));
        Assert.True(h.WaitResult("333") == "333", "M12 首推观测≠333\n" + h.Diagnostics());

        // 重推同体：0 变更
        var r2 = h.PushProbeBody("function scr_e2e_probe() { return 333; }");
        Assert.True(r2.Attempted, "M12 重推未启动：" + string.Join("；", r2.Failures) + "\n" + h.Diagnostics());
        Assert.True(r2.Succeeded, "M12 重推失败：" + string.Join("；", r2.Failures) + "\n" + h.Diagnostics());
        // 观测值不变（仍是 333）
        Assert.True(h.WaitResult("333") == "333", "M12 重推后观测变\n" + h.Diagnostics());
    }

    /// <summary>M13：厨房水槽单批——probe 新体（新局部+新实例变量+新字符串）+
    /// 新脚本槽 + 对象事件 + RoomCC 混一批。真实「mod 升级」形态。</summary>
    [E2EFact]
    public void M13_KitchenSink_SingleBatch()
    {
        h.Boot("m13_kitchen");

        var r = h.PushProduct(product =>
        {
            // 新脚本槽
            Msl.AddFunction("function scr_e2e_newcmd() { return \"ks_新字符串\"; }", "scr_e2e_newcmd");
            // 探针新体（调新脚本 + 新局部 + 新实例变量）
            var probe = product.Code.First(c => c.Name.Content == TestDataBuilder.ProbeScript);
            probe.ReplaceGML(
                "function scr_e2e_probe() { var _x = 10; ks_ivar = _x + 5;\nreturn scr_e2e_newcmd(); }",
                product);
            // 对象事件
            var step = product.Code.First(c => c.Name.Content == "gml_Object_oE2ETarget_Step_0");
            step.ReplaceGML("global.e2e_target = global.e2e_target + 100;", product);
            // RoomCC
            var cc = product.Rooms.First(r2 => r2.Name.Content == "START").CreationCodeId!;
            cc.ReplaceGML("global.e2e_roomcc = 99;", product);
        });

        Assert.True(r.Attempted, "M13 热通道未启动：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(r.Succeeded, "M13 厨房水槽热推失败：" + string.Join("；", r.Failures) + "\n" + h.Diagnostics());
        Assert.True(h.WaitResult("ks_新字符串") == "ks_新字符串",
            "M13 厨房水槽热语义断裂（期望 ks_新字符串）\n" + h.Diagnostics());
    }

    public void Dispose() => h.Dispose();
}
