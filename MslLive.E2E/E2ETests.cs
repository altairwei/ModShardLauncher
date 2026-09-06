using System.Text;
using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
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

    public void Dispose() => h.Dispose();
}
