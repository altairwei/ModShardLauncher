using System.Collections.Generic;
using System.Linq;
using UndertaleModLib;

namespace ModShardLauncher.HotReload;

public enum AssetKind
{
    Sprite, Object, Room, Sound, Font, Path, Background, Timeline, Shader,
    Script, Sequence, AnimCurve, ParticleSystem, RoomInstance,
}

/// <summary>每类资产池的「新增区间」[baseline.Count, product.Count)。
/// 落在区间内的 pushi.e 字面量才需要定类翻译；baseline 区间内恒等、超出 product 上界视为普通常量。</summary>
public sealed class AssetRanges
{
    readonly (AssetKind Kind, int Lo, int HiExcl)[] ranges;

    public AssetRanges(params (AssetKind Kind, int Lo, int HiExcl)[] ranges) => this.ranges = ranges;

    public static AssetRanges Empty { get; } = new();

    public static AssetRanges From(UndertaleData baseline, UndertaleData product)
    {
        var list = new List<(AssetKind, int, int)>
        {
            (AssetKind.Sprite, baseline.Sprites.Count, product.Sprites.Count),
            (AssetKind.Object, baseline.GameObjects.Count, product.GameObjects.Count),
            (AssetKind.Room, baseline.Rooms.Count, product.Rooms.Count),
            (AssetKind.Sound, baseline.Sounds.Count, product.Sounds.Count),
            (AssetKind.Font, baseline.Fonts.Count, product.Fonts.Count),
            (AssetKind.Path, baseline.Paths.Count, product.Paths.Count),
            (AssetKind.Background, baseline.Backgrounds.Count, product.Backgrounds.Count),
            (AssetKind.Timeline, baseline.Timelines.Count, product.Timelines.Count),
            (AssetKind.Shader, baseline.Shaders.Count, product.Shaders.Count),
        };
        return new AssetRanges(list.Where(r => r.Item3 > r.Item2).ToArray());
    }

    public IReadOnlyList<AssetKind> CandidateKinds(long value) =>
        ranges.Where(r => value >= r.Lo && value < r.HiExcl).Select(r => r.Kind).ToList();

    /// <summary>product 池内按索引取资产名（载荷的 assets 表用）。</summary>
    public static string? AssetName(UndertaleData data, AssetKind kind, long index)
    {
        int i = (int)index;
        try
        {
            switch (kind)
            {
                case AssetKind.Sprite: return i >= 0 && i < data.Sprites.Count ? data.Sprites[i].Name.Content : null;
                case AssetKind.Object: return i >= 0 && i < data.GameObjects.Count ? data.GameObjects[i].Name.Content : null;
                case AssetKind.Room: return i >= 0 && i < data.Rooms.Count ? data.Rooms[i].Name.Content : null;
                case AssetKind.Sound: return i >= 0 && i < data.Sounds.Count ? data.Sounds[i].Name.Content : null;
                case AssetKind.Font: return i >= 0 && i < data.Fonts.Count ? data.Fonts[i].Name.Content : null;
                case AssetKind.Path: return i >= 0 && i < data.Paths.Count ? data.Paths[i].Name.Content : null;
                case AssetKind.Background: return i >= 0 && i < data.Backgrounds.Count ? data.Backgrounds[i].Name.Content : null;
                case AssetKind.Timeline: return i >= 0 && i < data.Timelines.Count ? data.Timelines[i].Name.Content : null;
                case AssetKind.Shader: return i >= 0 && i < data.Shaders.Count ? data.Shaders[i].Name.Content : null;
                default: return null;
            }
        }
        catch { return null; }
    }
}
