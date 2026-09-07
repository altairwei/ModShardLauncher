using System.Collections.Generic;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 3] 本轮 Save 记账（每轮 ResetRound——Task 6 起由 FastPushCore 在轮首调）：
/// entry → 终稿文本。读序第一站（链上家）。</summary>
public static class FinalTextStore
{
    struct Rec { public ModShardLauncher.PatchingWay Way; public string Text; }

    static readonly Dictionary<string, Rec> map = new();

    public static int Count => map.Count;

    public static void ResetRound() => map.Clear();

    public static void Record(string entryName, ModShardLauncher.PatchingWay way, string finalText)
        => map[entryName] = new Rec { Way = way, Text = finalText };

    public static bool Contains(string entryName) => map.ContainsKey(entryName);

    public static bool TryGet(string entryName, out string text, out ModShardLauncher.PatchingWay way)
    {
        if (map.TryGetValue(entryName, out var r)) { text = r.Text; way = r.Way; return true; }
        text = ""; way = default; return false;
    }

    /// <summary>快照枚举（Task 6 编译阶段用）。</summary>
    public static IEnumerable<(string Entry, ModShardLauncher.PatchingWay Way, string Text)> Entries
    {
        get { foreach (var (k, v) in map) yield return (k, v.Way, v.Text); }
    }
}
