using ModShardLauncher;
using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

public class DevModeTests : IDisposable
{
    readonly bool savedDev = Main.Settings.DevMode;
    readonly int savedSlots = Main.Settings.LiveScriptSlots;
    readonly string savedParents = Main.Settings.LiveShellParents;

    public void Dispose()
    {
        // Main.Settings 是进程级静态——恢复原值，避免污染其他测试
        Main.Settings.DevMode = savedDev;
        Main.Settings.LiveScriptSlots = savedSlots;
        Main.Settings.LiveShellParents = savedParents;
    }

    [Fact]
    public void DevMode_DefaultOff()
    {
        Main.Settings.DevMode = false;
        Assert.False(DevMode.Active);
    }

    [Fact]
    public void Quotas_FromSettings()
    {
        Main.Settings.LiveScriptSlots = 7;
        Main.Settings.LiveShellParents = "a,b,,";
        var q = DevMode.Quotas;
        Assert.Equal(7, q.ScriptSlots);
        Assert.Equal(new[] { "a", "b", "", "" }, q.ShellParents);
    }

    [Fact]
    public void ReportResult_Inactive_IsSilent()
    {
        Main.Settings.DevMode = false;
        DevMode.ReportResult(new HotPushResult());   // 不抛即为通过（无 UI 依赖路径）
    }
}
