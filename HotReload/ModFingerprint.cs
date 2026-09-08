using System.Collections.Generic;
using System.Linq;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 7] mod 集合指纹：启用 mod 名 + 顺序拼接（顺序也算——重排=重编译语义）。
/// birth 在工作图出生后第一次快推记（RunPush 尾 Record）；生命周期与账本同步
/// （OnDataReloaded / 异哈希锁清时 Reset——新工作图/新会话后首推重新注册）。
/// 顺序铁律：Verify 先、Record 后——反序自证清白。</summary>
public static class ModFingerprint
{
    static bool recorded;
    static string birth = "";

    /// <summary>生产不传 → 走 ModInfos.Instance 启用集；Instance null（headless/单测）且未传 → ""。
    /// 可选列表参 = 单测缝（Instance 在测试宿主恒 null）。</summary>
    public static string Capture(IEnumerable<string>? enabled = null)
    {
        enabled ??= Controls.ModInfos.Instance?.Mods.Where(m => m.isEnabled).Select(m => m.Name);
        return enabled == null ? "" : string.Join("\n", enabled);
    }

    public static void RecordBirth(IEnumerable<string>? enabled = null)
    {
        birth = Capture(enabled);
        recorded = true;
    }

    public static void Reset()
    {
        recorded = false;
        birth = "";
    }

    /// <summary>未记录（首推前）放行——本轮尾 Record 注册；已记录 → 当前集合与 birth 逐字比对。</summary>
    public static bool Verify(IEnumerable<string>? enabled = null)
    {
        if (!recorded) return true;
        return Capture(enabled) == birth;
    }
}
