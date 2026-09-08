using ModShardLauncher.HotReload;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 8] 滞后退出提示的纯判定（三键弹窗语义由 WPF MessageBox 承载，不进单测）。</summary>
public class ExitPromptTests
{
    [Fact]
    public void NeedsPrompt_Lag0_False()
    {
        Assert.False(LagPrompt.NeedsPrompt(0));
    }

    [Fact]
    public void NeedsPrompt_PositiveLag_True()
    {
        Assert.True(LagPrompt.NeedsPrompt(1));
        Assert.True(LagPrompt.NeedsPrompt(37));
    }
}
