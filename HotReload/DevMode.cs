using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace ModShardLauncher.HotReload;

/// <summary>Dev 模式门控（spec §8）。Active/Quotas/ShellBuckets 在 Task 9 落地（BuildAndPush 编译依赖）；
/// 本文件其余成员（ReportResult/OnAppExit）与 UI/流程挂钩在 Task 10 补齐。</summary>
public static class DevMode
{
    public static bool Active => Main.Settings.DevMode;

    public static LiveQuotas Quotas => new()
    {
        ScriptSlots = Main.Settings.LiveScriptSlots,
        ShellObjects = Main.Settings.LiveShellObjects,
        EmptyRooms = Main.Settings.LiveEmptyRooms,
        BlankSprites = Main.Settings.LiveBlankSprites,
        BlankPaths = Main.Settings.LiveBlankPaths,
        ShellParents = Main.Settings.LiveShellParents.Split(','),
    };

    /// <summary>壳桶表（壳运行时索引, 父类名）——从 BootBaseline 找 o_msl_shell_N；无 baseline → 空表。</summary>
    public static IReadOnlyList<(int, string)> ShellBuckets()
    {
        var boot = BaselineStore.BootBaseline?.Product;
        if (boot == null) return Array.Empty<(int, string)>();
        return boot.GameObjects.Select((o, i) => (o, i))
            .Where(t => t.o.Name.Content.StartsWith("o_msl_shell_"))
            .OrderBy(t => t.o.Name.Content)
            .Select(t => (t.i, t.o.ParentId?.Name?.Content ?? ""))
            .ToList();
    }

    /// <summary>三态反馈（spec §8）：纯写盘 / 写盘+热更 / 写盘成功+热更失败（原因+建议动作）。</summary>
    public static void ReportResult(HotPushResult r)
    {
        if (!Active) return;
        string msg;
        if (!r.Attempted)
            msg = r.Failures.Count > 0
                ? $"纯写盘（无热会话：{string.Join("；", r.Failures)}）"
                : "纯写盘（无热会话）";
        else if (r.Succeeded)
            msg = $"写盘 + 热更成功：{r.AppliedEntries} entries，{r.ElapsedMs} ms";
        else
            msg = $"写盘成功 + 热更失败：{string.Join("；", r.Failures)}" +
                  (r.RequiresRestart ? "——建议重启游戏（双输出保证重启即最新）" : "——建议检查代码后重试");
        Log.Information("[live] {msg}", msg);
        Controls.ModInfos.Instance?.SetLiveStatus(msg);
    }

    /// <summary>退出清理（spec §4.3）：会话 End + baseline 释放；bootstrap DLL 清理挂钩在 Task 15 补。</summary>
    public static void OnAppExit()
    {
        LiveSession.Current?.End();
        BaselineStore.Reset();
    }
}
