using System.Collections.Generic;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 3] entry → (最后一次编译进工作图的文本哈希, PatchingWay)——「只编译改动」判据。
/// 判据锚工作图不锚产出（spec §5.2）。清零咽喉见 FastPushContext.OnDataReloaded（触发①）与
/// OnBaselineLocked（触发②，同哈希重锁不清 §7.7）。</summary>
public static class CompileLedger
{
    struct Entry { public ModShardLauncher.PatchingWay Way; public string Hash; }

    static readonly Dictionary<string, Entry> map = new();

    public static int Count => map.Count;

    public static void MarkCompiled(string entryName, ModShardLauncher.PatchingWay way, string textHash)
        => map[entryName] = new Entry { Way = way, Hash = textHash };

    public static bool IsCurrent(string entryName, string textHash)
        => map.TryGetValue(entryName, out var e) && e.Hash == textHash;

    public static bool Contains(string entryName) => map.ContainsKey(entryName);

    public static bool TryGet(string entryName, out ModShardLauncher.PatchingWay way, out string textHash)
    {
        if (map.TryGetValue(entryName, out var e)) { way = e.Way; textHash = e.Hash; return true; }
        way = default; textHash = ""; return false;
    }

    /// <summary>快照枚举（Task 6 回滚集遍历用）。</summary>
    public static IEnumerable<(string Entry, ModShardLauncher.PatchingWay Way, string Hash)> Entries
    {
        get { foreach (var (k, v) in map) yield return (k, v.Way, v.Hash); }
    }

    public static void Clear() => map.Clear();
}
