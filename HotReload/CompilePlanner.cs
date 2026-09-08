using System;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public sealed record PlannedCompile(string Entry, ModShardLauncher.PatchingWay Way, string Text);
public sealed record FastPushPlan(
    List<PlannedCompile> Compiles, List<PlannedCompile> Rollbacks, List<string> SkippedRollbacks);

/// <summary>[v2 Task 6] 纯计划：终稿存储（本轮 .sml 重放产物）∩ 账本（已编译进工作图的文本）
/// → 变更集/回滚集。只做判定不动图；Execute 才编译。快照语义：调用方执行前 Plan 已冻结，
/// 执行中状态变化不影响本轮判定。</summary>
public static class CompilePlanner
{
    public static FastPushPlan Plan()
    {
        var compiles = new List<PlannedCompile>();
        var rollbacks = new List<PlannedCompile>();
        var skipped = new List<string>();

        // 变更集：终稿哈希 ≠ 账本哈希（账本空 = 档1 全编）
        foreach (var (entry, way, text) in FinalTextStore.Entries)
            if (!CompileLedger.IsCurrent(entry, TextHash.Hash(text)))
                compiles.Add(new PlannedCompile(entry, way, text));

        // 回滚集：账本有、本轮无人写、缓存有 vanilla 体、且图内还残留非 vanilla 体
        // （账本哈希 == 缓存 vanilla 哈希 → 上次已回滚过 → 跳过——防每轮白编）。
        // asm 途径一律跳过（Task 6 裁决，FastText 首触快照同款边界）：disassemble→assemble
        // 往返不保证逐字节还原，回滚宁缺毋滥——SkippedRollbacks 诚实列出。
        foreach (var (entry, way, ledgerHash) in CompileLedger.Entries)
        {
            if (FinalTextStore.Contains(entry)) continue;   // 本轮有人写 → 上面变更集已处理
            if (way != ModShardLauncher.PatchingWay.GML) { skipped.Add(entry); continue; }
            if (!DecompileCache.TryGet(entry, way, out var vanilla)) { skipped.Add(entry); continue; }
            if (string.Equals(ledgerHash, TextHash.Hash(vanilla), StringComparison.Ordinal)) continue;
            rollbacks.Add(new PlannedCompile(entry, way, vanilla!));
        }
        return new FastPushPlan(compiles, rollbacks, skipped);
    }

    /// <summary>执行计划（编译进工作图）。fail-closed：任何一条失败抛给上层（FastPushCore 记 failure
    /// 终止）；账本只在编译成功后 Mark——失败的条目自然没进账本，下轮回路自愈。回滚编译后账本记
    /// vanilla 哈希（图内现状语义）→ 后续轮被上面的「账本==缓存」判据挡住，不重复回滚。</summary>
    public static void Execute(FastPushPlan plan)
    {
        foreach (var c in plan.Compiles)
        {
            var entry = GraphEntry(c.Entry);
            FastText.CompileEntry(entry, c.Text, c.Way);
            CompileLedger.MarkCompiled(c.Entry, c.Way, TextHash.Hash(c.Text));
        }
        foreach (var r in plan.Rollbacks)
        {
            var entry = GraphEntry(r.Entry);
            FastText.CompileEntry(entry, r.Text, r.Way);
            CompileLedger.MarkCompiled(r.Entry, r.Way, TextHash.Hash(r.Text));
        }
    }

    static UndertaleCode GraphEntry(string entryName)
        => ModLoader.Data.Code.FirstOrDefault(c => c.Name.Content == entryName)
           ?? throw new InvalidOperationException("账本/终稿条目不在工作图里: " + entryName);
}
