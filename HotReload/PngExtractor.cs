using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ModShardLauncher.HotReload;

public sealed class StripInfo
{
    public string RelPath { get; set; } = "";   // 相对游戏目录（MSL 侧断言用；loader 文本不内嵌它——路径运行时拼接）
    public int Frames { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
}

public sealed class PngExtractException : Exception
{
    public PngExtractException(string msg) : base(msg) { }
}

public static class PngExtractor
{
    /// <summary>loader 里引用的相对路径根（spec §7.3：游戏目录旁，sandbox 相对路径可读）。</summary>
    public const string ResRelRoot = "mods/_live/res";

    public static void WriteBlankPng(string resDirAbs)
    {
        Directory.CreateDirectory(resDirAbs);
        string path = Path.Combine(resDirAbs, "_blank.png");
        if (File.Exists(path)) return;
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.Transparent);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>把一个变更精灵的全部帧 PNG 从内存中的 ModFile 块取出，合成 strip 写到 resDirAbs。
    /// 文件名 = 运行时索引（loader 的 GML 只能拼索引路径，见 Task 5）。返回 loader 用的相对路径与元数据。
    /// 任何一帧找不到 → PngExtractException（fail-closed）。</summary>
    public static StripInfo EnsureSpritePngs(SpriteChange c, IReadOnlyList<LiveTextureEntry> scan,
        IReadOnlyList<ModFile> mods, string resDirAbs, int runtimeIndex)
    {
        Directory.CreateDirectory(resDirAbs);
        var frames = scan.Where(e => e.SpriteName == c.Name).OrderBy(e => e.Frame).ToList();
        if (frames.Count == 0)
            throw new PngExtractException($"sprite {c.Name}: no source PNG in current scan (纯 C# 构造的精灵 v1 不支持热更)");

        var images = new List<Bitmap>();
        try
        {
            foreach (var f in frames)
            {
                var mod = mods.FirstOrDefault(m => m.Name == f.ModName)
                    ?? throw new PngExtractException($"sprite {c.Name}: mod {f.ModName} not loaded");
                byte[] png = mod.GetFile(f.FileName);
                if (png.Length == 0)
                    throw new PngExtractException($"sprite {c.Name}: empty PNG block {f.FileName} in {f.ModName}");
                images.Add(new Bitmap(new MemoryStream(png)));
            }
            int w = images.Max(i => i.Width), h = images.Max(i => i.Height);
            string rel = $"{ResRelRoot}/{runtimeIndex}.png";
            using (var strip = new Bitmap(w * images.Count, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(strip))
                    for (int i = 0; i < images.Count; i++)
                        g.DrawImageUnscaled(images[i], i * w, 0);
                strip.Save(Path.Combine(resDirAbs, $"{runtimeIndex}.png"), ImageFormat.Png);
            }
            return new StripInfo { RelPath = rel, Frames = images.Count, OriginX = c.Product.OriginX, OriginY = c.Product.OriginY };
        }
        finally { foreach (var i in images) i.Dispose(); }
    }
}
