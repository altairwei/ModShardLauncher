using System.Text.RegularExpressions;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 8] 拒批文案诚实化钉子（spec §9 修订，用户已批准）：
/// 改类（载荷形态/数据问题——重启救不了）三处不再说「需重启」；
/// 保留类（重启真能救：写盘后随盘载入重建）四处「重启」建议原样保留；
/// 快推语境下批构建失败前置「先完整编译」指引（4 处同串）。
/// 断言打在源码文件上（异常文案是插值串，程序集里无逐字形态）。</summary>
public class HonestCopyTests
{
    static string HotPipelineSource => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "HotReload", "HotPipeline.cs"));

    [Fact]
    public void PayloadClassSites_NoLongerClaimRestartHelps()
    {
        var src = HotPipelineSource;
        // 改类三处（原 222/701/707）：载荷形态/数据问题——重启后同样失败
        Assert.DoesNotContain("槽载荷需 wrapper——需重启", src);
        Assert.DoesNotContain("未知——需重启", src);
        Assert.DoesNotContain("无法路由——需重启", src);
    }

    [Fact]
    public void PayloadClassSites_NewHonestCopyPresent()
    {
        var src = HotPipelineSource;
        Assert.Contains("修复载荷或移除该改动后重试（重启无法解决）", src);   // 三处同尾（222/701/707）
        Assert.Equal(3, Regex.Matches(src, "修复载荷或移除该改动后重试（重启无法解决）").Count);
    }

    [Fact]
    public void QuotaClassSites_RestartAdviceRetained()
    {
        var src = HotPipelineSource;
        // 保留类（v1 写盘流：盘上已有——重启即载入）：变量无来源（172）/壳事件（207）/
        // 新增资产 v1 不热更（714）/无 boot 池（747）
        Assert.Contains("重启游戏后即可正常载入", src);
        Assert.Contains("壳事件菜单不含", src);
        Assert.Contains("v1 不热更——需重启", src);
        Assert.Contains("无 boot 池——需重启", src);
    }

    [Fact]
    public void FastPushGuidance_LeadsEveryUnwrittenDiskFailurePath()
    {
        var src = HotPipelineSource;
        // 4 处：3 个早退（TryConnect 失败/ForRunningGame 异常/boot 基线空）+ 批构建失败补位（Task 8）
        Assert.Equal(4, Regex.Matches(src, "快推未写盘——先做一次完整编译并重启游戏").Count);
    }
}
