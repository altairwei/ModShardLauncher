using System;
using System.IO;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 7] vanilla 文件绊线：data.win 路径/内容在两次快推之间被动过就拒。
/// 快查 = mtime/size 未变直接过（省哈希）；变了才重开文件 MD5 复核（同内容 mtime 抖动容忍——
/// 复核过就追平 mtime/size）。生命周期与指纹/账本同步（OnDataReloaded / 异哈希锁清时 Reset）。</summary>
public static class VanillaTripwire
{
    static string? path;
    static string hash = "";
    static long len = -1;
    static DateTime mtime;

    public static void Record()
    {
        path = DataLoader.dataPath;
        hash = DataLoader.VanillaHash ?? "";
        if (string.IsNullOrEmpty(path)) { len = -1; mtime = default; return; }
        var fi = new FileInfo(path);
        len = fi.Length;
        mtime = fi.LastWriteTimeUtc;
    }

    /// <summary>未记录（首推前）放行——本轮尾 Record 注册（与指纹同款首推注册语义；
    /// 计划草稿此处原为「未记录→拒」，但 Record 只在 Verify 之后调——照抄会永久自锁）。
    /// 已记录：路径切换即拒；文件 mtime/size 变 → MD5 复核不符拒。</summary>
    public static bool Verify(out string reason)
    {
        reason = "";
        if (path == null) return true;
        if (!string.Equals(path, DataLoader.dataPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = "data.win 路径变了——重新打开并完整编译";
            return false;
        }
        if (string.IsNullOrEmpty(path)) return true;
        var fi = new FileInfo(path);
        if (fi.Length == len && fi.LastWriteTimeUtc == mtime) return true;   // 快查
        string now;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            now = DataLoader.ComputeFileHash(fs);
        if (now != hash)
        {
            reason = "data.win 变了（哈希不符）——重新打开并完整编译";
            return false;
        }
        len = fi.Length;
        mtime = fi.LastWriteTimeUtc;   // 追平（同内容 mtime 抖动的容忍）
        return true;
    }

    public static void Reset()
    {
        path = null;
        hash = "";
        len = -1;
        mtime = default;
    }
}
