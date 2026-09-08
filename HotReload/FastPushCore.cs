using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Serilog;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 6] 快推轮产出（spec §9 状态行素材）：拒绝（前置门未过，未 Attempt）/
/// 尝试（轮已跑）+ 编译/推送/滞后计数 + 失败明细。轮内逐步组装——round 字段可写；
/// Rejected/RejectionReason/Attempted 创建即定。</summary>
public sealed class FastPushOutcome
{
    public bool Rejected { get; init; }          // 绊线拒绝（未 Attempt）
    public string? RejectionReason { get; init; }
    public bool Attempted { get; init; }
    public bool Succeeded { get; set; }
    public int CompiledEntries { get; set; }
    public int PushedEntries { get; set; }
    public int LagEntries { get; set; }
    public List<string> Failures { get; } = new();
    public long ElapsedMs { get; set; }
}

/// <summary>[v2 Task 6] 快推编排（生产入口 RunPush + 轮核心 PushCompiled + E2E 缝）。
/// RunPush 前置门全过才动图：Dev 开 / 有写盘基线 / 有工作图 / 活会话（#25——会话在但目标死
/// 也拒：重连是写盘流 BuildAndPushCore 的职责，快推零编译预算不容白烧）。指纹/绊线门在
/// Task 7 挂进 CheckGates。</summary>
public static class FastPushCore
{
    /// <summary>裁决 9 判据（headless 可测）：工作图带快推漂移（滞后>0）或被快推碰过
    ///（账本非空）→ 全量编译前必须回精源（spec §4）。无快推过的会话恒 false → 零成本跳过。</summary>
    internal static bool NeedsPristineReload => FastPushContext.LagCount > 0 || CompileLedger.Count > 0;

    /// <summary>生产入口（UI 线程同步调）。门不过 → Rejected + 指引（状态行）；全链异常
    /// #31 式兜底：不抛、记 Failures。</summary>
    public static FastPushOutcome RunPush()
    {
        var sw = Stopwatch.StartNew();
        string? reject = CheckGates();
        if (reject != null)
        {
            sw.Stop();
            Log.Information("[live] fast-push rejected: {reason}", reject);
            Controls.ModInfos.Instance?.SetLiveStatus("快推未执行：" + reject);
            return new FastPushOutcome { Rejected = true, RejectionReason = reject, ElapsedMs = sw.ElapsedMilliseconds };
        }

        var outcome = new FastPushOutcome { Attempted = true };
        try
        {
            // 重载 .sml（LoadFiles 自带保留启用态：old.isEnabled 继承）
            ModLoader.LoadFiles();
            // [v2 Task 7 挂点] ModFingerprint.RecordBirth() + VanillaTripwire.Record()（LoadFiles 后：集合状态是新集合）
            var round = PushCompiled();
            return round;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[live] fast-push 未捕获异常——记失败返回（不写盘不推送）");
            outcome.Failures.Add($"快推失败：{ex.GetType().Name}: {ex.Message}");
            sw.Stop();
            outcome.ElapsedMs = sw.ElapsedMilliseconds;
            return outcome;
        }
    }

    /// <summary>轮核心（RunPush 与 E2E 缝共享）。runPatchFile=false：E2E 缝形态——跳过轮首
    /// 重置与 PatchFile（.sml 重放需 ModInfos UI 宿主，由冒烟覆盖；E2E 钉终稿→编译→推送→
    /// 账本→滞后管线本身，finals 由 CompileAndPushWith 预记）。</summary>
    internal static FastPushOutcome PushCompiled(bool runPatchFile = true)
    {
        var outcome = new FastPushOutcome { Attempted = true };
        var sw = Stopwatch.StartNew();
        try
        {
            if (runPatchFile)
            {
                // 轮首重置（spec §6.2）：终稿存储只含本轮 .sml 重放产物；LootTables 只在
                // LoadFile 尾清——快推轮不 LoadFile，不清会跨轮重复累积（Credits/Disclaimers/
                // Menus 由 PatchMods 头部自重置，无需在此处理）
                FinalTextStore.ResetRound();
                LootUtils.ResetLootTables();
            }
            FastPushContext.BeginPush();
            try
            {
                if (runPatchFile)
                    ModLoader.PatchFile();   // 快推模式：Task 4/5 重定向后 = 现有条目记终稿不编译 + 结构 op 即建即记账本 + 注入幂等
                var plan = CompilePlanner.Plan();
                CompilePlanner.Execute(plan);   // fail-closed：任何条目抛 → 上抛，本轮不写盘不推送
            }
            finally { FastPushContext.EndPush(); }

            // 免写盘推送：Register 全跳过（写盘基线只由真实写盘点维护）；会话重连 #25 逻辑
            // 保留（前置门已保证 Active——此处连不上只会是门后竞态，失败走 Failures 指引）
            var push = HotPipeline.BuildAndPush(DataLoader.data, DataLoader.savedDataPath, register: false);
            foreach (var f in push.Failures)
                outcome.Failures.Add(f);

            // 滞后刷新（裁决 8）：基面 = Latest（最后一次写盘记录），不是 BootBaseline——
            // 游戏运行中全量编译后 boot=旧镜像而 Latest=新盘，用 boot 当滞后会永久虚高并
            // 误触退出提示。register=false 从不 Register → Latest 恒为上一次真实写盘。
            var latest = BaselineStore.Latest;
            if (latest != null)
                FastPushContext.LagCount = CodeDiffer.Diff(latest.Product, DataLoader.data).Count;

            outcome.Succeeded = outcome.Failures.Count == 0 && push.Succeeded;
            outcome.PushedEntries = push.AppliedEntries;
        }
        catch (Exception ex)
        {
            // 编译/PatchFile 异常 = 本轮作废：不写盘、不推送（上面 BuildAndPush 未达）。
            // 已成功编译+记账的条目保留（图内现状语义）——下轮 Plan 对未编条目自愈重排。
            Log.Error(ex, "[live] fast-push round failed（fail-closed：不写盘不推送）");
            outcome.Failures.Add($"快推失败：{ex.Message}");
        }
        finally
        {
            sw.Stop();
            outcome.ElapsedMs = sw.ElapsedMilliseconds;
            outcome.CompiledEntries = FastPushContext.CompileCount;   // EndPush 不清计数——轮值轮末取
            outcome.LagEntries = FastPushContext.LagCount;
        }
        return outcome;
    }

    /// <summary>E2E 缝（Task 9）：终稿预记 + 免 PatchFile 的轮核心——钉终稿→编译→推送→
    /// 账本→滞后管线本身。</summary>
    internal static FastPushOutcome CompileAndPushWith(
        params (string Entry, ModShardLauncher.PatchingWay Way, string Text)[] finals)
    {
        FinalTextStore.ResetRound();
        foreach (var (entry, way, text) in finals)
            FinalTextStore.Record(entry, way, text);
        return PushCompiled(runPatchFile: false);
    }

    /// <summary>前置门（顺序 = 拒因优先级）：任一不过返回拒绝理由，全过返回 null。
    /// Task 7 在 Dev 门后挂指纹/绊线门。</summary>
    static string? CheckGates()
    {
        if (!DevMode.Active)
            return "未开 Dev 模式——设置里开启后才能快推";
        if (string.IsNullOrEmpty(DataLoader.savedDataPath))
            return "先做一次完整编译（快推需要写盘基线）";
        if (DataLoader.data?.FORM == null)
            return "先打开 data.win";
        if (LiveSession.Current is not { State: LiveSessionState.Active } session
            || !session.TargetStillRunning())
            return "快推只对活会话生效——游戏未运行/会话已死，用完整编译写盘";
        return null;
    }
}
