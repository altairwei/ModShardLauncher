using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UndertaleModLib;
using Serilog;

namespace ModShardLauncher.HotReload;

public sealed class CompileRecord
{
    public string Hash { get; set; } = "";
    public string Gen8 { get; set; } = "";
    public UndertaleData Product { get; set; } = null!;
    public List<LiveTextureEntry> Scan { get; set; } = new();
    public DateTime Time { get; set; }
    public Lazy<Dictionary<string, int>> StrgIndex { get; set; } = null!;
}

/// <summary>spec §4.5：MSL 给每次编译产物算哈希并保留快照；握手按游戏上报的 boot 哈希锁定
/// baseline——不靠时序假设。Dev 关闭或会话结束时 Reset 释放（spec §4.3「退出时缓存释放」）。
/// 容量 = pinned BootBaseline + 滚动最近 3 条（窗口外的游戏 boot 版本 → 诚实拒绝重启）。</summary>
public static class BaselineStore
{
    const int WindowSize = 3;

    public static CompileRecord? Latest { get; private set; }
    public static CompileRecord? BootBaseline { get; private set; }
    static readonly List<CompileRecord> window = new();

    /// <summary>[v2 Task 3] LockBaseline 成功 pin 且 isNewGameSession 时 raise（参数 = 被 pin 的哈希）。
    /// 同哈希重锁早退不 raise（§7.7 漂移重放）；窗口 miss 不 raise。订阅侧判定哈希真变才清账本。</summary>
    public static event Action<string>? BaselineLocked;

    public static CompileRecord Register(UndertaleData product, string savedFilePath,
        IReadOnlyList<LiveTextureEntry> scan)
    {
        string hash;
        using (var fs = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            hash = Convert.ToHexString(SHA256.Create().ComputeHash(fs));
        SlimDown(product);
        var rec = new CompileRecord
        {
            Hash = hash,
            Gen8 = Gen8Guard.VersionOf(product),
            Product = product,
            Scan = scan.ToList(),
            Time = DateTime.Now,
            StrgIndex = new Lazy<Dictionary<string, int>>(() =>
            {
                var d = new Dictionary<string, int>(product.Strings.Count);
                for (int i = 0; i < product.Strings.Count; i++)
                    d.TryAdd(product.Strings[i].Content, i);
                return d;
            }),
        };
        Latest = rec;
        window.RemoveAll(r => ReferenceEquals(r, BootBaseline));   // pinned 不占窗口位
        window.Add(rec);
        while (window.Count > WindowSize) window.RemoveAt(0);
        return rec;
    }

    /// <summary>清掉 diff/提取/编码用不到的重型 blob（贴图页图像、音频字节）。瘦身是内存优化，
    /// 不是正确性依赖——AUDO 走反射做 best-effort（vendored 旧版的 chunk/条目类型名不在编译期绑定），
    /// 任何一步失败只意味着多占内存，记 warning 继续。</summary>
    static void SlimDown(UndertaleData data)
    {
        try
        {
            foreach (var tex in data.EmbeddedTextures)
                tex.TextureData = null;   // GMImage 字节随引用断开可 GC
        }
        catch (Exception ex) { Log.Warning("[live] baseline slim(TXTR) skipped: {msg}", ex.Message); }
        try
        {
            if (data.FORM?.Chunks != null && data.FORM.Chunks.TryGetValue("AUDO", out var chunk))
            {
                if (chunk.GetType().GetProperty("List")?.GetValue(chunk) is System.Collections.IEnumerable items)
                    foreach (var item in items)
                    {
                        var dataProp = item.GetType().GetProperty("Data");
                        if (dataProp?.PropertyType == typeof(byte[]) && dataProp.CanWrite)
                            dataProp.SetValue(item, null);
                    }
            }
        }
        catch (Exception ex) { Log.Warning("[live] baseline slim(AUDO) skipped: {msg}", ex.Message); }
    }

    public static CompileRecord? LockBaseline(string hash, out bool isNewGameSession, out string reason)
    {
        isNewGameSession = false;
        if (BootBaseline != null && string.Equals(BootBaseline.Hash, hash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "";
            return BootBaseline;   // 同一游戏会话，继续用
        }
        var found = (Latest != null && string.Equals(Latest.Hash, hash, StringComparison.OrdinalIgnoreCase))
            ? Latest
            : window.FirstOrDefault(r => string.Equals(r.Hash, hash, StringComparison.OrdinalIgnoreCase));
        if (found == null)
        {
            reason = "游戏 boot 的 data.win 不在本会话基线窗口（游戏更新过，或 boot 后连续编译超过 " +
                     WindowSize + " 次且未推送）——用当前 MSL 编译一次并重启游戏";
            return null;
        }
        BootBaseline = found;
        window.Remove(found);
        isNewGameSession = true;
        reason = "";
        BaselineLocked?.Invoke(found.Hash);   // [v2 Task 3] 新 pin——订阅侧判定哈希是否真变
        return found;
    }

    /// <summary>[v2 Task 3] 测试缝：直接驱动事件（等价于 LockBaseline 命中新哈希）。</summary>
    internal static void RaiseBaselineLockedForTest(string hash) => BaselineLocked?.Invoke(hash);

    public static void Reset()
    {
        Latest = null;
        BootBaseline = null;
        window.Clear();
    }
}
