using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 4] DSL 读序/记账门面。读序（spec §5.2）：FinalTextStore（仅快推窗内——
/// 链上家终稿，full 模式必须无视残稿）→ DecompileCache（双模式共用）→ 反编译工作图（真相源）；
/// 冷路径反编译后按三门槛（vanilla 名 ∧ 账本无 ∧ 非本轮脏）决定是否入缓存。
/// full 模式行为不变性：冷路径与原 Decompiler.Decompile/Disassemble 同参同果，只多缓存写入与计数。</summary>
public static class FastText
{
    public static bool IsFastPush => FastPushContext.InFastPush;

    /// <summary>DSL 读取统一入口（LoadGML/LoadAssemblyAsString/GetStringGMLFromFile/
    /// GetAssemblyString/GetTable/SetTable 重定向到此）。</summary>
    public static string Read(UndertaleCode entry, string entryName, ModShardLauncher.PatchingWay way)
    {
        // 第一站：本轮终稿（链上家）——仅快推窗内。上一快推轮的残稿对全量编译是脏数据，
        // full 模式读者不得吃到（spec §7.2 真相源语义）。
        if (FastPushContext.InFastPush && FinalTextStore.TryGet(entryName, out string? final, out _))
            return final;

        // 第二站：反编译缓存——双模式共用（装载换血/基线换血时整体作废，见 FastPushContext）。
        if (DecompileCache.TryGet(entryName, way, out string? cached))
            return cached;

        // 第三站：反编译工作图。[v2 Task 1] 计时插桩随冷路径迁入此处（原 LoadGML/LoadAssemblyAsString 内联段）。
        string text;
        if (way == ModShardLauncher.PatchingWay.AssemblyAsString)
        {
            using (new PhaseClock("disassemble: " + entryName))
                text = entry.Disassemble(ModLoader.Data.Variables, ModLoader.Data.CodeLocals.For(entry));
        }
        else
        {
            using (new PhaseClock("decompile: " + entryName))
                text = Decompiler.Decompile(entry, new GlobalDecompileContext(ModLoader.Data, false));
        }
        PerfCounters.CountDecompile();

        // 可缓存三门槛：vanilla 名（会话基线体）∧ 账本无记录（未被快推改过）∧ 非本轮脏
        // （full 补丁链内后跑 mod 读先跑 mod 改过的条目，不得把其产物当 vanilla 存毒）。
        if (FastPushContext.IsVanillaName(entryName)
            && !CompileLedger.Contains(entryName)
            && !FastPushContext.IsDirty(entryName))
        {
            DecompileCache.Store(entryName, way, text);
        }
        return text;
    }

    /// <summary>本轮脏标记（Save/SetTable/AddCode 等实际变异点统一入口；full 模式也调——
    /// 缓存门控不分模式）。语义即 FastPushContext.NoteDirty。</summary>
    public static void NoteMutated(string entryName) => FastPushContext.NoteDirty(entryName);

    /// <summary>快推轮终稿记账（Save/SetStringGMLInFile/SetAssemblyString/SetTable 的 fast 终端）。
    /// last-writer-wins；Task 6 编译阶段按账本判增量。</summary>
    public static void RecordFinal(string entryName, ModShardLauncher.PatchingWay way, string finalText)
        => FinalTextStore.Record(entryName, way, finalText);

    /// <summary>编译进工作图（仅快推编译阶段 Execute 调用；full 模式 Save 走原 ReplaceGML 路径不经此）。</summary>
    public static void CompileEntry(UndertaleCode entry, string text, ModShardLauncher.PatchingWay way)
    {
        string entryName = entry.Name?.Content ?? "";
        // 首触快照：本会话第一次编译该「vanilla 名」条目时，先把图内现状反编译入缓存。
        // 快推窗内非账本条目的图状态恒等于装载后的 vanilla 体（图变异只经 Execute——都进账本），
        // 所以此刻的现状就是可安全回滚的 vanilla 原文。没有它，SetStringGMLInFile 类「直改不读」
        // 的条目在编辑从 .sml 消失时无 vanilla 可回（SkippedRollbacks——诚实边界，但常见
        // 「改了又删」回路不该落在这里）。GML-only：asm 途径回滚本就跳过（见 Task 6 裁决）。
        if (way == ModShardLauncher.PatchingWay.GML
            && FastPushContext.IsVanillaName(entryName)
            && !CompileLedger.Contains(entryName)
            && !FastPushContext.IsDirty(entryName))
        {
            string pre = Decompiler.Decompile(entry, new GlobalDecompileContext(ModLoader.Data, false));
            DecompileCache.Store(entryName, way, pre);
        }
        switch (way)
        {
            case ModShardLauncher.PatchingWay.GML:
                entry.ReplaceGML(text, ModLoader.Data);
                break;
            case ModShardLauncher.PatchingWay.AssemblyAsString:
                Msl.CheckInstructionsVariables(entry, text);
                string locals = AssemblyWrapper.CreateLocalVarAssemblyAsString(entry);
                string pre2 = text.Insert(text.IndexOf('\n') + 1, locals);
                entry.Replace(Assembler.Assemble(pre2, ModLoader.Data));
                break;
        }
        PerfCounters.CountCompile();
        FastPushContext.CompileCount++;   // 轮次编译计数（E2E/状态行断言用）
        FastPushContext.NoteDirty(entryName);   // 脏集——阻止本轮后续 Read 把已改体当 vanilla 存
    }
}
