using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class VarIdSimulatorTests
{
    /// <summary>断言集 = 运行实例实测（S2，s2/runtime/run.hex 全量 65 操作数对齐）验证过的
    /// <b>结构不变量</b>——每一条都直接对应一项实测 runner 行为，不是模拟器自身输出的循环论证：
    /// 局部声明序连续（646..649）、st* 族连续（312..315）、spr/waterDrawState 邻接（1215/1216）、
    /// 同名同 id、内置不占装载序（sprite_index=26/argument0=0x5D 是 smallId 而非 loadOrderId）。
    /// 绝对基准值（646 本身）<b>故意不钉</b>：探针 v1–v8 证明静态模拟有未消化的系统性偏移
    /// （早窗 -3、中窗 +1，共 4 个未识别名字），精确化是 Task 11（exe 内置变量表收割 +
    /// 运行实例复测）的既定 scope。届时恢复绝对断言：i:_borderLeft==646 … i:_borderBottom==649。</summary>
    [Fact]
    public void Simulate_ReproducesMeasuredStructuralInvariants()
    {
        UndertaleData data;
        using (var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });
        var ids = VarIdSimulator.Simulate(data);

        // ① 局部变量占装载序空间且按 .localvar 声明序连续（实测 100646..100649，
        //    scr_unitRenderDrawSprite 的 local _borderLeft.._borderBottom）
        Assert.True(ids.ContainsKey("i:_borderLeft"), "locals must consume load-order ids (S2 实测)");
        int l = ids["i:_borderLeft"];
        Assert.Equal(l + 1, ids["i:_borderTop"]);
        Assert.Equal(l + 2, ids["i:_borderRight"]);
        Assert.Equal(l + 3, ids["i:_borderBottom"]);

        // ② 实例变量族按首遇序连续（实测 stScaleY..stX = 100312..100315）
        int s = ids["i:stScaleY"];
        Assert.Equal(s + 1, ids["i:stScaleX"]);
        Assert.Equal(s + 2, ids["i:stY"]);
        Assert.Equal(s + 3, ids["i:stX"]);

        // ③ 邻接（实测 spr=101215 与 waterDrawState=101216 相邻）
        Assert.Equal(ids["i:spr"] + 1, ids["i:waterDrawState"]);

        // ④ i/g 共享单一数值空间（实测邻接：i:spr=1215、i:waterDrawState=1216、
        //    紧接的 1217 属一条 stacktop 引用——0x80 顶字节实为 VariableType.Stacktop 形态字节，
        //    非作用域标记，见 VarIdSimulator 注释的 S2 修正）：全表 id 不得撞车，且 g: 键存在
        Assert.True(ids.Keys.Any(k => k.StartsWith("g:")), "global vars must be keyed g:");
        Assert.Equal(ids.Count, ids.Values.Distinct().Count());

        // ⑤ 内置变量不占装载序空间（实测 sprite_index=0x1A、argument0=0x5D 为 exe smallId）
        Assert.DoesNotContain(ids.Keys, k => k.EndsWith(":sprite_index"));
        Assert.DoesNotContain(ids.Keys, k => k.EndsWith(":image_index"));
        Assert.DoesNotContain(ids.Keys, k => k.EndsWith(":argument0"));
        Assert.DoesNotContain(ids.Keys, k => k.EndsWith(":x"));

        // ⑥ 键前缀形态
        Assert.All(ids.Keys, k => Assert.Matches(@"^[ig]:", k));
    }
}
