using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MslLive.Shared;
using Serilog;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

public sealed class BuildBatchResult
{
    public BatchMsg? Batch { get; set; }
    public List<string> Failures { get; } = new();
    public bool RequiresRestart { get; set; }
}

public sealed class HotPushResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int AppliedEntries { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Failures { get; } = new();
    public bool RequiresRestart { get; set; }
}

/// <summary>热更编排（spec §6.3 的 MSL 侧落地）：diff → 提取 → overlay 改写 → loader 合并 → 推送。
/// agent 因此保持全哑——载荷里每个名字在 boot 运行时都可解析（product-only 的全部改写到
/// 槽/壳/空房间载体）。整批 fail-closed：任何 entry 失败 → Batch=null 不推（改动互相调用的
/// 版本错位正是 spec §5.2 要防的事故；写盘已成功，dev 修完重来零成本）。</summary>
public static class HotPipeline
{
    static readonly Regex ObjectEventName = new(@"^gml_Object_(.+)_(Create|Destroy|Step|Alarm|Collision|Keyboard|Mouse|Other|Draw|PreCreate|CleanUp|Trigger|Broadcast|Animation|Async)_(\d+)$", RegexOptions.Compiled);
    static readonly Regex RoomCcName = new(@"^gml_RoomCC_(r_msl_empty_\d+|.+?)(?:_\d+)?(?:_Create)?$", RegexOptions.Compiled);

    public static BuildBatchResult BuildBatch(UndertaleData boot, UndertaleData product,
        SessionState alloc, IReadOnlyList<LiveTextureEntry> curScan, IReadOnlyList<LiveTextureEntry> bootScan)
    {
        var result = new BuildBatchResult();
        try { Gen8Guard.Check(boot, product); }
        catch (InvalidOperationException ex) { result.Failures.Add(ex.Message); return result; }

        var ranges = AssetRanges.From(boot, product);
        var resolver = new AssetKindResolver();
        var changed = CodeDiffer.Diff(boot, product);
        var assetDiff = AssetDiffer.Diff(boot, product, curScan, bootScan);
        if (assetDiff.SoundFontPathContentChanged)
        {
            result.RequiresRestart = true;
            result.Failures.Add("sound/font/path 数量变化——v1 不热更该类资源内容（代码照常推送）");
        }

        var bootCodeNames = new HashSet<string>(boot.Code.Select(c => c.Name.Content));
        // 函数可解析性判据：boot 的 code 名 ∪ FUNC 函数名。内建函数（draw_sprite 等）没有 code
        // entry，只看 code 名会把它们误判成 product-only 而去占槽——必须并上 boot.Functions。
        var bootFnNames = new HashSet<string>(bootCodeNames);
        foreach (var f in boot.Functions) bootFnNames.Add(f.Name.Content);
        var strg = new Dictionary<string, int>(boot.Strings.Count);
        for (int i = 0; i < boot.Strings.Count; i++) strg.TryAdd(boot.Strings[i].Content, i);

        var ops = new List<OpMsg>();
        int seq = 0;

        // ---- 资产 loader 片段（精灵 → 壳配置；spec §7.4 顺序）----
        var loaderFragments = new List<string>();
        var shellsBefore = new HashSet<string>(alloc.ObjectShells.Keys);
        int entryFailures = 0;   // 只有 entry/资产失败计入；SoundFontPathContentChanged 是警告不中断
        // resDirAbs/enabledMods 惰性求值：无精灵变更时不需要（测试里 ModInfos.Instance 是 null）
        string? resDirAbs = null;
        IReadOnlyList<ModFile>? enabledMods = null;
        foreach (var sc in assetDiff.Sprites)
        {
            try
            {
                resDirAbs ??= ResDirAbs();
                enabledMods ??= EnabledMods();
                int runtimeIndex = sc.IsNew ? alloc.AllocateSprite(sc.Name) : sc.ProductIndex;
                var strip = PngExtractor.EnsureSpritePngs(sc, curScan, enabledMods, resDirAbs, runtimeIndex);
                loaderFragments.Add(LoaderGen.SpriteLoader(sc, strip, runtimeIndex));
            }
            catch (Exception ex) when (ex is PoolExhaustedException or PngExtractException)
            {
                entryFailures++;
                result.Failures.Add($"sprite {sc.Name}: {ex.Message}");
                if (ex is PoolExhaustedException) result.RequiresRestart = true;
            }
        }

        // ---- code entries（ManagerStepEntry 单独处理：有 loader 时由 trigger/restore 覆盖）----
        var stepEntry = changed.FirstOrDefault(e => e.Name == LiveStubInjector.ManagerStepEntry);
        foreach (var entry in changed)
        {
            if (ReferenceEquals(entry, stepEntry)) continue;
            try
            {
                var op = BuildSwapOp(entry, boot, product, ranges, resolver, bootCodeNames, bootFnNames, strg, alloc, ref seq);
                ops.Add(op);
            }
            catch (Exception ex) when (ex is AssetAmbiguityException or PoolExhaustedException or OverlayException)
            {
                entryFailures++;
                result.Failures.Add(ex.Message);
                if (ex is PoolExhaustedException or OverlayException) result.RequiresRestart = true;
            }
        }

        if (entryFailures > 0)
        {
            // fail-closed：有 entry 失败 → 整批不推（计划既定决策，见任务说明）
            result.Batch = null;
            return result;
        }

        // ---- 壳配置片段（本批新分配的壳；对象有 sprite 时 set_sprite）----
        foreach (var (objName, shellIdx) in alloc.ObjectShells)
        {
            if (shellsBefore.Contains(objName)) continue;
            int? spriteIdx = ObjectSpriteRuntimeIndex(objName, boot, product, alloc);
            loaderFragments.Add(LoaderGen.ShellConfig(objName, shellIdx, spriteIdx));
        }

        // ---- RunGml：有 loader 片段 → [trigger, restore, loader]；否则 stepEntry 普通 swap ----
        if (loaderFragments.Count > 0)
        {
            string text = LoaderGen.MergeOrdered(loaderFragments);
            ops.InsertRange(0, BuildLoaderOps(text, boot, strg, ref seq));
        }
        else if (stepEntry != null)
        {
            ops.Insert(0, BuildSwapOp(stepEntry, boot, product, ranges, resolver, bootCodeNames, bootFnNames, strg, alloc, ref seq));
        }

        result.Batch = new BatchMsg { Ops = ops };
        return result;
    }

    // 对象 sprite 的运行时索引：无 sprite → null；baseline 既有 → boot 恒等索引；新精灵 → 本批空白分配
    static int? ObjectSpriteRuntimeIndex(string objName, UndertaleData boot, UndertaleData product, SessionState alloc)
    {
        var pObj = product.GameObjects.FirstOrDefault(o => o.Name.Content == objName);
        string? sprName = pObj?.Sprite?.Name?.Content;
        if (sprName == null) return null;
        var bSpr = boot.Sprites.FirstOrDefault(s => s.Name.Content == sprName);
        if (bSpr != null) return boot.Sprites.IndexOf(bSpr);
        return alloc.SpriteBlanks.TryGetValue(sprName, out int idx) ? idx : null;
    }

    static OpMsg BuildSwapOp(ChangedEntry entry, UndertaleData boot, UndertaleData product,
        AssetRanges ranges, AssetKindResolver resolver, HashSet<string> bootCodeNames,
        HashSet<string> bootFnNames, Dictionary<string, int> strg, SessionState alloc, ref int seq)
    {
        string targetEntry = entry.Name;
        bool slotTargeted = false;
        var m = ObjectEventName.Match(entry.Name);
        if (m.Success && !ObjectExistsIn(boot, m.Groups[1].Value))
        {
            // product-only 对象的事件 → 壳 retarget
            string objName = m.Groups[1].Value;
            var pObj = product.GameObjects.First(o => o.Name.Content == objName);
            alloc.AllocateShell(objName, pObj.ParentId?.Name?.Content);
            targetEntry = $"gml_Object_o_msl_shell_{ShellSlotOf(boot, alloc, objName)}_{m.Groups[2].Value}_{m.Groups[3].Value}";
            if (!bootCodeNames.Contains(targetEntry))
                throw new OverlayException($"{entry.Name}: 壳事件菜单不含 {m.Groups[2].Value}_{m.Groups[3].Value}——需重启");
        }
        else if (!bootCodeNames.Contains(entry.Name))
        {
            if (entry.Name.StartsWith("gml_RoomCC_"))
            {
                int slot = alloc.AllocateRoom(RoomNameOf(entry.Name));
                targetEntry = $"gml_RoomCC_r_msl_empty_{slot}_0";
            }
            else
            {
                // product-only 脚本 → 槽位。载荷必须是 wrapper 形态（function 声明编译产物
                // [B][body][exit][tail]——调用面 = 子入口 +4，S2②/S2④）；裸体形态（无子条目，
                // 旧 AddFunction("return 42;") 风格）在产品里本就 runtime 不可调用——fail-closed。
                if (entry.Product.ChildEntries.Count == 0)
                    throw new OverlayException($"{entry.Name}: 非函数声明形态（无子条目）——槽载荷需 wrapper——需重启");
                slotTargeted = true;
                targetEntry = alloc.AllocateScript(ScriptSlotKey(entry.Name));
            }
        }

        var payload = RefsExtractor.Extract(entry.Product, product, ranges, resolver);
        var op = new OpMsg
        {
            Seq = seq++, Kind = "swap", Entry = targetEntry,
            // LocalsCount：wrapper 形态取偏移 4 主子的值（函数体真值——编译器对子条目取
            // patch.LocalsCount=distinct；根值是根作用域公式恒 1，无信息量）；普通条目取自身
            // （事件/CC 走 AddCode 的「+1 for arguments」公式 = 1+distinct，与 boot 同式可直接比较）
            LocalsCount = (int)(entry.Product.ChildEntries.FirstOrDefault(c => c.Offset == 4)?.LocalsCount
                ?? entry.Product.LocalsCount),
            Instructions = payload.Instructions,
            Variables = payload.Variables,   // 变量名不改写：agent 用 VarsMsg 映射，未映射即拒
            Functions = OverlayFunctions(payload.Functions, bootFnNames, alloc),
        };
        if (slotTargeted)
        {
            // 尾部两处重写：绑定尾的 fnref/self-var 从产品名改到槽名——槽名在 boot 可解析
            string bare = BareScriptName(entry.Name);
            RewriteBindingTail(payload.Instructions,
                "gml_Script_" + bare, "gml_Script_" + targetEntry, bare, targetEntry);
        }
        foreach (var s in payload.Strings)
            op.Strings.Add(new StrRef { Content = s, StrgIndex = strg.GetValueOrDefault(s, -1) });
        foreach (var sem in op.Instructions)
            if (sem.Fn != null && alloc.ScriptSlots.TryGetValue(ScriptSlotKey(sem.Fn), out var slot))
                sem.Fn = "gml_Script_" + slot;   // S2②：调用操作数 = 100000+子 codeId——裸槽名会解析到根（绑定尾）而非函数体
        foreach (var a in payload.Assets)
            op.Assets.Add(ResolveAsset(a, boot, product, alloc));
        return op;
    }

    /// <summary>RunGml 三 op（Task 15 的 agent 语义）：trigger 把 step entry 换成
    /// 「正常体 + msl_loader_0()」；restore 换回正常体；loader 换 msl_loader_0 本体
    /// （#16b：loader 载荷改 scratch wrapper 编译——槽 stub 是 function 声明形态，
    /// 调用面 = 子入口 +4，平文本载荷会把子入口落进指令中间）。
    /// 全部对 boot baseline 编译（名字在 boot 全部可解析；字符串播种由 Task 8 保证）。
    /// ReplaceGML 对全部存在的名字/字符串是非变异的——前后计数做触发线，变了就记警告
    /// （意味着播种/模板漏了什么，属于 bug 而非静默通过）。</summary>
    internal static List<OpMsg> BuildLoaderOps(string gmlText, UndertaleData boot, Dictionary<string, int> strg, ref int seq)
    {
        var ops = new List<OpMsg>();
        ops.Add(CompileTextOp(LiveStubInjector.StepEventGml + "\nmsl_loader_0();",
            LiveStubInjector.ManagerStepEntry, "trigger", boot, strg, ref seq));
        ops.Add(CompileTextOp(LiveStubInjector.StepEventGml,
            LiveStubInjector.ManagerStepEntry, "restore", boot, strg, ref seq));
        ops.Add(CompileLoaderOp(gmlText, LiveStubInjector.LoaderSlot, boot, strg, ref seq));
        return ops;
    }

    static OpMsg CompileTextOp(string text, string entry, string kind, UndertaleData boot,
        Dictionary<string, int> strg, ref int seq)
    {
        int strCount = boot.Strings.Count, varCount = boot.Variables.Count;
        var code = new UndertaleCode { Name = boot.Strings.MakeString(entry) };
        code.ReplaceGML(text, boot);
        if (boot.Strings.Count != strCount || boot.Variables.Count != varCount)
            Log.Warning("[live] loader compile mutated baseline: str {a}->{b}, var {c}->{d}（播种/模板漏名字=bug）",
                strCount, boot.Strings.Count, varCount, boot.Variables.Count);
        var payload = RefsExtractor.Extract(code, boot, AssetRanges.Empty, new AssetKindResolver());
        // LocalsCount 自设 = 1+distinct 局部（TypeInst=-7 的相异 Var）：裸 entry 编译出的
        // LocalsCount=0 是谎（tw-shape [2d] 实证——「+1 for arguments」公式只在 AddCode
        // 预建 LOCZ 的条目上生效）；目标条目（事件）的 boot 值 = 同公式 1+distinct，
        // 两侧同式才能过 agent 的 ≤ 容量检查（trigger 1 ≤ Step 事件 boot 1 精确）
        int distinctLocals = payload.Instructions
            .Where(i => i.Inst == -7 && i.Var != null).Select(i => i.Var!).Distinct().Count();
        var op = new OpMsg
        {
            Seq = seq++, Kind = kind, Entry = entry, ExecuteOnce = kind is "trigger" or "restore",
            LocalsCount = 1 + distinctLocals,
            Instructions = payload.Instructions, Variables = payload.Variables,
            Functions = payload.Functions,
        };
        foreach (var s in payload.Strings)
            op.Strings.Add(new StrRef { Content = s, StrgIndex = strg.GetValueOrDefault(s, -1) });
        return op;
    }

    static int scratchSeq;

    /// <summary>loader 载荷：`function &lt;scratch&gt;() { &lt;loader 文本&gt; }` 在全新 scratch 根上
    /// 编译 → 取根 sems（完整 wrapper [B][body][exit][tail]）→ 尾部两处重写 scratch→真名 →
    /// LocalsCount = scratch 子条目值（loader 体 _t 一个局部 = 1，与垫片 stub 子=1 匹配）。
    /// isNewFunc 无去重——每次全新 scratch 名；根必须先入 Code 列表再编译（子插在
    /// Code[IndexOf(root)+1]，不入列则子落列表头 = 地址腐化）；用毕移除 scratch 根+子
    /// （boot.Code 复原；编译期追加的字符串/VARI/FUNC 为 append-only 留置，无害）。
    /// scratch 名是全新的 → 编译必变异（计数触发线在此失真）——播种检查改为逐载荷字符串
    /// boot 可解析（StrgIndex ≥ 0；比计数更精确：变异来自 scratch 名，播种缺口来自 loader 文本）。</summary>
    static OpMsg CompileLoaderOp(string gmlText, string realName, UndertaleData boot,
        Dictionary<string, int> strg, ref int seq)
    {
        string scratch = $"msl_scratch_{scratchSeq++}";
        var root = new UndertaleCode { Name = boot.Strings.MakeString(scratch) };
        boot.Code.Add(root);
        root.ReplaceGML($"function {scratch}()\n{{\n{gmlText}\n}}", boot);
        var child = root.ChildEntries.FirstOrDefault(c => c.Offset == 4);
        var payload = RefsExtractor.Extract(root, boot, AssetRanges.Empty, new AssetKindResolver());
        RewriteBindingTail(payload.Instructions,
            "gml_Script_" + scratch, "gml_Script_" + realName, scratch, realName);
        var op = new OpMsg
        {
            Seq = seq++, Kind = "loader", Entry = realName,
            LocalsCount = (int)(child?.LocalsCount ?? root.LocalsCount),
            Instructions = payload.Instructions, Variables = payload.Variables,
            Functions = payload.Functions,
        };
        foreach (var s in payload.Strings)
            op.Strings.Add(new StrRef { Content = s, StrgIndex = strg.GetValueOrDefault(s, -1) });
        if (child != null) boot.Code.Remove(child);
        boot.Code.Remove(root);
        return op;
    }

    // 指令字形态常量（= MslLive.Agent.BcEncoder 同名值；MSL 侧不引 agent 程序集，
    // RefsExtractor 同款风格——值本身就是 SemInstruction 线上契约）
    const byte OpPush = 0xC0, OpPop = 0x45;
    const byte TInt32 = 2, TVariable = 5;

    /// <summary>绑定尾两处重写（fnref push.i 与 self-var pop.v.v）。绑定尾 = 末 9 指令
    /// （编译器 isNewFunc 钉版形态：push.v fnref/conv/pushi.e -1/conv/call.v method argc=2/
    /// dup/pushi.e -6/pop.v.v/popz.v）；体内同名引用不落此窗（递归调用是 OpCall、self 写
    /// 是 Self inst——形态互异）。载荷尾部引用产品/scratch 名时 agent 侧不可解析——必须
    /// 改写到 boot 在场的名字（槽名/真名）。名字对不上 = 无重写 → agent 拒绝（fail-closed）。</summary>
    static void RewriteBindingTail(List<SemInstruction> insts, string oldFn, string newFn, string oldVar, string newVar)
    {
        foreach (var sem in insts.Skip(Math.Max(0, insts.Count - 9)))
        {
            if (sem.Kind == OpPush && sem.T1 == TInt32 && sem.Fn == oldFn)
                sem.Fn = newFn;
            else if (sem.Kind == OpPop && sem.T1 == TVariable && sem.Var == oldVar)
                sem.Var = newVar;
        }
    }

    /// <summary>脚本名裸化：剥 gml_Script_ / gml_GlobalScript_ 前缀（都不带则原样）。</summary>
    static string BareScriptName(string name) => name switch
    {
        var n when n.StartsWith("gml_Script_") => n.Substring("gml_Script_".Length),
        var n when n.StartsWith("gml_GlobalScript_") => n.Substring("gml_GlobalScript_".Length),
        var n => n,
    };

    /// <summary>槽键规范化：一律 'gml_Script_' + 裸名 = 调用点 sem.Fn 的形态（S2②：脚本
    /// 调用操作数 = 100000+子 codeId，FUNC 名 = 'gml_Script_X'）。分配（脚本自身 op）与
    /// 查询（调用点重定向）共用同一键形态——产品根裸名（AddCode）与调用引用（gml_Script_
    /// 前缀）不会各占一槽。</summary>
    static string ScriptSlotKey(string name) => "gml_Script_" + BareScriptName(name);

    // ---- overlay 小 helper ----

    /// <summary>product-only 函数名 → 槽（键 = ScriptSlotKey 形态，与调用点重定向同一键）；
    /// 列表值与 sems 重定向后同形（'gml_Script_' + 槽名）。boot 可解析（code 名 ∪ FUNC 名）
    /// 的原样。</summary>
    static List<string> OverlayFunctions(IReadOnlyList<string> names, HashSet<string> bootFnNames, SessionState alloc)
        => names.Select(n => bootFnNames.Contains(n) ? n
            : "gml_Script_" + alloc.AllocateScript(ScriptSlotKey(n))).ToList();

    /// <summary>资产引用翻译：baseline 区间恒等；新增区间按 kind 分配运行时载体
    /// （Sprite→空白 / Object→壳 / Room→空房间；其余 → OverlayException 需重启）。</summary>
    static AssetRef ResolveAsset(AssetRef a, UndertaleData boot, UndertaleData product, SessionState alloc)
    {
        if (!Enum.TryParse<AssetKind>(a.Kind, out var kind))
            throw new OverlayException($"asset ref kind '{a.Kind}' 未知——需重启");
        if (a.Index < BootCount(boot, kind))
        {
            a.RuntimeIndex = a.Index;
            return a;
        }
        string name = a.Name ?? throw new OverlayException($"{a.Kind}[{a.Index}] 无名字，无法路由——需重启");
        a.RuntimeIndex = kind switch
        {
            AssetKind.Sprite => alloc.AllocateSprite(name),
            AssetKind.Object => alloc.AllocateShell(name,
                product.GameObjects.FirstOrDefault(o => o.Name.Content == name)?.ParentId?.Name?.Content),
            AssetKind.Room => alloc.AllocateRoom(name),
            _ => throw new OverlayException($"{a.Kind} 新增资产（{name}）v1 不热更——需重启"),
        };
        return a;
    }

    static bool ObjectExistsIn(UndertaleData boot, string name) =>
        boot.GameObjects.Any(o => o.Name.Content == name);

    /// <summary>AllocateShell 之后从壳的运行时对象索引反查槽序号 N（壳名 o_msl_shell_N）。</summary>
    static int ShellSlotOf(UndertaleData boot, SessionState alloc, string objName)
    {
        int runtimeIndex = alloc.ObjectShells[objName];
        string shellName = boot.GameObjects[runtimeIndex].Name.Content;
        return int.Parse(shellName.Substring("o_msl_shell_".Length));
    }

    static string RoomNameOf(string entryName)
    {
        var m = RoomCcName.Match(entryName);
        return m.Success ? m.Groups[1].Value : entryName;
    }

    static int BootCount(UndertaleData boot, AssetKind kind) => kind switch
    {
        AssetKind.Sprite => boot.Sprites.Count,
        AssetKind.Object => boot.GameObjects.Count,
        AssetKind.Room => boot.Rooms.Count,
        AssetKind.Sound => boot.Sounds.Count,
        AssetKind.Font => boot.Fonts.Count,
        AssetKind.Path => boot.Paths.Count,
        AssetKind.Background => boot.Backgrounds.Count,
        AssetKind.Timeline => boot.Timelines.Count,
        AssetKind.Shader => boot.Shaders.Count,
        _ => throw new OverlayException($"asset kind {kind} 无 boot 池——需重启"),
    };

    /// <summary>res 目录绝对路径：savedDataPath 目录 + mods/_live/res，无 savedDataPath 时用游戏目录。
    /// 两者皆空 = 还没写盘就在推——不可能来自正常流程，fail-closed。</summary>
    static string ResDirAbs()
    {
        string? p = !string.IsNullOrEmpty(DataLoader.savedDataPath) ? DataLoader.savedDataPath
            : !string.IsNullOrEmpty(DataLoader.dataPath) ? DataLoader.dataPath : null;
        if (p == null) throw new InvalidOperationException("res 目录定位失败：savedDataPath/dataPath 皆空（尚未写盘）");
        return Path.Combine(Path.GetDirectoryName(p) ?? ".", PngExtractor.ResRelRoot);
    }

    static IReadOnlyList<ModFile> EnabledMods() =>
        Controls.ModInfos.Instance.Mods.Where(m => m.isEnabled).ToList();

    /// <summary>写盘后的热通道编排（IO 层）：注册 baseline → 保会话 → BuildBatch → PushBatch。
    /// 任何一步失败 = 纯写盘降级（写盘已成功，游戏重启即最新，spec §8 三态）。</summary>
    public static HotPushResult BuildAndPush(UndertaleData product, string savedFilePath)
    {
        var result = new HotPushResult();
        if (!DevMode.Active) return result;

        PngExtractor.WriteBlankPng(ResDirAbs());   // GameStart 的 blank 分配在游戏启动时就要它存在
        BaselineStore.Register(product, savedFilePath, TextureLoader.LiveScan);
        var session = LiveSession.Current;
        if (session == null || session.State != LiveSessionState.Active)
        {
            try
            {
                session = LiveSession.ForRunningGame(DevMode.Quotas, DevMode.ShellBuckets);
                if (!session.TryConnect()) { result.Failures.Add(session.LastError); return result; }
            }
            catch (Exception ex) { result.Failures.Add($"无热会话：{ex.Message}"); return result; }
        }
        if (BaselineStore.BootBaseline == null) { result.Failures.Add("boot baseline 未锁定"); return result; }

        result.Attempted = true;
        var sw = Stopwatch.StartNew();
        var build = BuildBatch(BaselineStore.BootBaseline.Product, product, session.Alloc!,
            TextureLoader.LiveScan, BaselineStore.BootBaseline.Scan);
        if (build.Batch == null)
        {
            foreach (var f in build.Failures) result.Failures.Add(f);
            result.RequiresRestart = build.RequiresRestart;
            return result;
        }
        if (build.Batch.Ops.Count == 0) { result.Succeeded = true; return result; }   // 无变更

        var receipt = session.PushBatch(build.Batch);
        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        if (receipt == null) { result.Failures.Add(session.LastError); return result; }
        result.Succeeded = receipt.AllOk;
        result.AppliedEntries = receipt.Ops.Count(o => o.Ok);
        result.RequiresRestart = receipt.Ops.Any(o => o.RequiresRestart);
        foreach (var o in receipt.Ops.Where(o => !o.Ok))
            result.Failures.Add($"[{o.Entry}] {o.Stage}: {o.Reason}");
        return result;
    }
}

public sealed class OverlayException : Exception
{
    public OverlayException(string msg) : base(msg) { }
}
