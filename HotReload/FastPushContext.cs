using System;
using System.Collections.Generic;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 2] 快推会话状态：fast 标志 + 每轮脏集/编译计数 + 装载挂钩（滞后归零/缓存换血/vanilla 名集快照）。
/// static ctor 订阅 DataLoader.DataLoaded——DataLoader 侧零耦合；测试用 ResetForTest 复原。
/// Task 3 起在 OnDataReloaded 里补 CompileLedger.Clear()（账本文件到该任务才存在）。</summary>
public static class FastPushContext
{
    public static bool InFastPush { get; private set; }
    public static int LagCount { get; set; }
    public static int CompileCount { get; set; }

    static readonly HashSet<string> roundDirty = new();
    static readonly HashSet<string> vanillaNames = new();
    static string? cachedHash;
    static string? lastPinnedHash;

    static FastPushContext()
    {
        DataLoader.DataLoaded += OnDataReloaded;
        BaselineStore.BaselineLocked += OnBaselineLocked;
    }

    internal static void BeginPush()
    {
        InFastPush = true;
        CompileCount = 0;
        roundDirty.Clear();
    }

    internal static void EndPush()
    {
        InFastPush = false;
        roundDirty.Clear();
    }

    /// <summary>本轮图变异标记（Save/SetTable/CompileEntry 等实际变异点调用——Task 4 接线）。
    /// 无条件记录（不 gate InFastPush）：full 模式补丁链内「后跑 mod 读先跑 mod 改过的条目」同样
    /// 要靠脏集拦缓存存毒；脏集在 OnDataReloaded（新装载=新图）与 BeginPush/EndPush（轮界）清空。</summary>
    internal static void NoteDirty(string entryName) => roundDirty.Add(entryName);
    internal static bool IsDirty(string entryName) => roundDirty.Contains(entryName);

    /// <summary>每次成功装载（LoadUmt 尾触发，无参——读 DataLoader.data）：滞后归零、
    /// vanilla 名集快照刷新、脏集清空、哈希变化 → 缓存整体作废、账本清零（触发①：工作图替换）。</summary>
    internal static void OnDataReloaded()
    {
        if (!string.Equals(cachedHash, DataLoader.VanillaHash, StringComparison.Ordinal))
        {
            DecompileCache.Clear();
            cachedHash = DataLoader.VanillaHash;
        }
        LagCount = 0;
        roundDirty.Clear();
        vanillaNames.Clear();
        if (DataLoader.data?.Code != null)
            foreach (var c in DataLoader.data.Code)
                if (c?.Name?.Content != null) vanillaNames.Add(c.Name.Content);
        CompileLedger.Clear();   // 触发①：工作图换血，旧账本全部作废
        ModFingerprint.Reset();   // [v2 Task 7] 指纹/绊线与账本同生命周期——新图首推重新注册
        VanillaTripwire.Reset();
    }

    /// <summary>LockBaseline 命中新哈希（触发②）。
    /// 首次 pin 只记不追（该会话首个基线与账本同生，装载时已清过）；此后 pin 到
    /// 不同哈希 = 新 boot 镜像（游戏重载了新文件）→ 账本/缓存整体作废（救援层按新基线重判）。
    /// 同哈希重锁走 LockBaseline 早退，事件根本不 raise（§7.7 漂移重放不清账本）。</summary>
    static void OnBaselineLocked(string hash)
    {
        if (lastPinnedHash != null && lastPinnedHash != hash)
        {
            CompileLedger.Clear();
            DecompileCache.Clear();
            ModFingerprint.Reset();   // [v2 Task 7] 新 boot 镜像 = 新会话形态——指纹/绊线同账本作废
            VanillaTripwire.Reset();
        }
        lastPinnedHash = hash;
    }

    internal static bool IsVanillaName(string entryName) => vanillaNames.Contains(entryName);

    public static void ResetForTest()
    {
        InFastPush = false;
        LagCount = 0;
        CompileCount = 0;
        roundDirty.Clear();
        vanillaNames.Clear();
        cachedHash = null;
        lastPinnedHash = null;
        DecompileCache.Clear();
        ModFingerprint.Reset();
        VanillaTripwire.Reset();
    }
}
