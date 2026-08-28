using System;
using System.Collections.Generic;
using System.Linq;

namespace ModShardLauncher.HotReload;

/// <summary>Dev 模式门控（spec §8）。Task 9 只落地 HotPipeline.BuildAndPush 编译所需的三成员
/// （Active/Quotas/ShellBuckets）；ReportResult/OnAppExit/UI/流程挂钩在 Task 10 补齐。</summary>
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
}
