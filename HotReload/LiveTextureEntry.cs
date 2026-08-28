namespace ModShardLauncher.HotReload;

/// <summary>一次 compile 里 TextureLoader 触碰的（精灵, 帧, 源 PNG）记录。
/// 热通道用它定位源 PNG（Task 4）与做跨 compile 的帧内容对账（Task 3）。</summary>
public sealed class LiveTextureEntry
{
    public string SpriteName { get; set; } = "";
    public int Frame { get; set; }
    public string ModName { get; set; } = "";
    public string FileName { get; set; } = "";   // ModFile.GetFile 用的包内文件名
    public string Sha256 { get; set; } = "";     // 源 PNG 字节哈希（大写 hex）
}
