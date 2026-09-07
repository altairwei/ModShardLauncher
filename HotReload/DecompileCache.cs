using System;
using System.Collections.Generic;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 2] (entry, way) → vanilla 文本缓存。只存 vanilla 来源文本（门控在 FastText.Read，
/// Task 4 接线）；VanillaHash 变化整体 Clear（FastPushContext.OnDataReloaded）。
/// 键用全名 ModShardLauncher.PatchingWay，避免命名空间搬移。</summary>
public static class DecompileCache
{
    static readonly Dictionary<(string Entry, ModShardLauncher.PatchingWay Way), string> map = new();

    public static int Count => map.Count;

    public static bool TryGet(string entryName, ModShardLauncher.PatchingWay way, out string? text)
        => map.TryGetValue((entryName, way), out text);

    public static void Store(string entryName, ModShardLauncher.PatchingWay way, string text)
        => map[(entryName, way)] = text;

    public static void Clear() => map.Clear();
}
