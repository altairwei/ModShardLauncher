namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 8] 滞后退出提示的纯判定。三键语义由 MessageBox 直接承载
/// （Yes=CompileThenExit、No=ExitAnyway、Cancel=CancelClose——映射写进键文案，见 Language）。</summary>
public static class LagPrompt
{
    /// <summary>滞后 &gt; 0（内存领先磁盘）才需要问；0 滞后 = 与盘一致，原样退出。</summary>
    public static bool NeedsPrompt(int lagCount) => lagCount > 0;
}
