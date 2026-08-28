using System;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public sealed class SpriteChange
{
    public string Name { get; set; } = "";
    public int ProductIndex { get; set; }
    public int BaselineIndex { get; set; } = -1;
    public bool IsNew { get; set; }
    public bool FramesChanged { get; set; }
    public bool FieldsChanged { get; set; }
    public UndertaleSprite Product { get; set; } = null!;
}

public sealed class AssetDiff
{
    public List<SpriteChange> Sprites { get; } = new();
    public List<(AssetKind Kind, string Name, int ProductIndex)> NewAssets { get; } = new();
    /// <summary>声音/字体/路径的内容变更（v1 不支持热更 → 回执「需重启」）。</summary>
    public bool SoundFontPathContentChanged { get; set; }
}

public static class AssetDiffer
{
    public static AssetDiff Diff(UndertaleData baseline, UndertaleData product,
        IReadOnlyList<LiveTextureEntry> currentScan, IReadOnlyList<LiveTextureEntry> bootScan)
    {
        var diff = new AssetDiff();
        var baseSprites = NameMap(baseline.Sprites);
        var curBySprite = currentScan.GroupBy(e => e.SpriteName)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Frame, e => e.Sha256));
        var bootBySprite = bootScan.GroupBy(e => e.SpriteName)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Frame, e => e.Sha256));

        for (int i = 0; i < product.Sprites.Count; i++)
        {
            var p = product.Sprites[i];
            string name = p.Name.Content;
            if (!baseSprites.TryGetValue(name, out var b))
            {
                diff.Sprites.Add(new SpriteChange
                { Name = name, ProductIndex = i, IsNew = true, FramesChanged = true, Product = p });
                continue;
            }
            int baseIndex = baseline.Sprites.IndexOf(b.Item);
            bool fields = FieldsDiffer(b.Item, p);
            bool frames = FramesDiffer(name, curBySprite, bootBySprite);
            if (fields || frames)
                diff.Sprites.Add(new SpriteChange
                { Name = name, ProductIndex = i, BaselineIndex = baseIndex, FieldsChanged = fields, FramesChanged = frames, Product = p });
        }

        CollectNew(diff, baseline.Sounds, product.Sounds, AssetKind.Sound, s => s.Name.Content);
        CollectNew(diff, baseline.Fonts, product.Fonts, AssetKind.Font, f => f.Name.Content);
        CollectNew(diff, baseline.Paths, product.Paths, AssetKind.Path, p => p.Name.Content);
        diff.SoundFontPathContentChanged = SoundFontPathChanged(baseline, product);
        return diff;
    }

    static bool FieldsDiffer(UndertaleSprite b, UndertaleSprite p) =>
        b.Width != p.Width || b.Height != p.Height
        || b.OriginX != p.OriginX || b.OriginY != p.OriginY
        || b.MarginLeft != p.MarginLeft || b.MarginRight != p.MarginRight
        || b.MarginTop != p.MarginTop || b.MarginBottom != p.MarginBottom;

    /// <summary>帧对账：只看本次 compile 触碰过的精灵（TextureLoader 路径），
    /// 与 boot compile 的同精灵帧哈希逐项比；帧集合不同也算变。</summary>
    static bool FramesDiffer(string name,
        Dictionary<string, Dictionary<int, string>> cur, Dictionary<string, Dictionary<int, string>> boot)
    {
        bool inCur = cur.TryGetValue(name, out var c);
        bool inBoot = boot.TryGetValue(name, out var b);
        if (!inCur && !inBoot) return false;          // 两compile都没碰 → 帧不可能变
        if (inCur != inBoot) return true;             // 一侧开始/停止提供 PNG
        if (c!.Count != b!.Count) return true;
        foreach (var (frame, hash) in c)
            if (!b.TryGetValue(frame, out var bh) || bh != hash) return true;
        return false;
    }

    static Dictionary<string, (T Item, int _)> NameMap<T>(IList<T> list) where T : UndertaleNamedResource
    {
        var d = new Dictionary<string, (T, int)>();
        foreach (var x in list) if (x.Name?.Content is string n && !d.ContainsKey(n)) d[n] = (x, 0);
        return d;
    }

    static void CollectNew<T>(AssetDiff diff, IList<T> b, IList<T> p, AssetKind kind, Func<T, string> name)
        where T : UndertaleNamedResource
    {
        var names = new HashSet<string>(b.Select(name));
        for (int i = 0; i < p.Count; i++)
            if (!names.Contains(name(p[i]))) diff.NewAssets.Add((kind, name(p[i]), i));
    }

    /// <summary>v1 粗判：名字配对的 sound/font/path 任何一侧数量不同 → true。
    /// （字段级内容比对留给需要的那天；真实 mod 不改既有 sound/font/path。）</summary>
    static bool SoundFontPathChanged(UndertaleData baseline, UndertaleData product) =>
        baseline.Sounds.Count != product.Sounds.Count
        || baseline.Fonts.Count != product.Fonts.Count
        || baseline.Paths.Count != product.Paths.Count;
}
