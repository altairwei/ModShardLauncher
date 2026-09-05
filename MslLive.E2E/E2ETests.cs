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

    public void Dispose() => h.Dispose();
}
