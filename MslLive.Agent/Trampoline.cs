using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>把 dummy stub（#16a function 声明形态，68B wrapper）换成 trampoline。
/// 新 buffer 布局 = [exit.i 4B @0][trampoline 体 @4]：[push.v argument0（仅 report）]
/// + call.v 原生索引 + popz.v（丢返回值）+ return 0 收尾——apply 28B / report 36B。
/// 根入口 0 = exit（安全 no-op：wrapper 的绑定尾只在 boot 装载期跑一次，swap 后根入口
/// 不再有语义）；子入口 4 = StartOff 实证（S2④：调子 = 从偏移 4 进入执行子体）= 体本身。
/// 安装前要求 dummy 活 buffer 是 68B 且头 20B（[B jump=5]+return-0 收尾+[exit.i T1=Int32]）
/// 与编码全等——不等 = 编译器形态假设错了，fail-closed 不安装。形态真源 =
/// ModShardLauncherTest.StubDummy_CompilesTo_WrapperWithPushiZeroRetVBody + tw-shape 探针
/// （root len=68）。
/// call 操作数经 Translator 走 Registry.IndexOf（原生注册后的真实槽位），并与
/// NativeRegistration 捕获的索引交叉断言。旧 buffer 永不释放（S3 旧帧安全）。
/// 根/子执行记录共享同一 buffer（S2④：两条执行记录 +0x18 同指 BUF；#17 实证修正：
/// msl wrapper 根无节点，只有子一条记录）——交换循环按 BufPtr 收集全部共享记录。
/// #19 全量交换面：+0x08 长度、+0x18 buffer、+0x20 handler 表、+0x28 pcmap 四字段同换
/// （线程化代码 VM 派发走 +0x20 表，只换 buffer = 旧表把新字节当操作数静默空跑）；
/// 表/pcmap 由 TableBuilder 复刻 EnsureSpecialized 规则构建，安装前对旧表自证（fail-closed）。
/// 写序 +0x08→+0x18→+0x28→+0x20（buffer 先于表 = 安全窗，见 TableBuilder.WriteRecord）。
/// 幂等：进程内已安装的 stub 不重复安装（proof 重连重发不会把 trampoline 当 dummy 判死）。</summary>
public static class Trampoline
{
    public const string ApplyName = "msl_live_apply";
    public const string ReportName = "msl_live_report";

    static readonly HashSet<string> installed = new();

    /// <summary>最近一次失败的原因（PipeServer 拼装 proofAck.Error 用）。</summary>
    public static string LastError { get; private set; } = "";

    internal static void ResetForTest() { installed.Clear(); LastError = ""; }

    public static bool Install(Func<string, int>? registryIndexOf = null, Func<int, ulong>? slot = null)
    {
        slot ??= TableBuilder.RuntimeSlot;
        bool ok = InstallOne(ApplyName, NativeRegistration.ApplyIndex, withArg: false, registryIndexOf, slot);
        ok &= InstallOne(ReportName, NativeRegistration.ReportIndex, withArg: true, registryIndexOf, slot);
        return ok;
    }

    /// <summary>return-0 收尾的语义指令（dummy body 的完整内容）。</summary>
    internal static readonly SemInstruction[] ReturnZeroTail =
    {
        new() { Kind = BcEncoder.OpPushI, T1 = BcEncoder.TInt16, Low16 = 0 },          // pushi.e 0
        new() { Kind = BcEncoder.OpConv, T1 = BcEncoder.TInt32, T2 = BcEncoder.TVariable }, // conv.i.v
        new() { Kind = BcEncoder.OpRet, T1 = BcEncoder.TVariable },                    // ret.v
    };

    /// <summary>dummy wrapper 头 20B 的语义指令（[B jump=5]+收尾+[exit.i]）。
    /// B +5 跳过 body+exit 直达绑定尾（tw-shape 探针钉版：root len=68，头 20B 之后是
    /// 48B 绑定尾，操作数含运行时 codeId 不参与校验）。</summary>
    internal static readonly SemInstruction[] WrapperHead =
    {
        new() { Kind = BcEncoder.OpB, Jump = 5 },
        new() { Kind = BcEncoder.OpPushI, T1 = BcEncoder.TInt16, Low16 = 0 },
        new() { Kind = BcEncoder.OpConv, T1 = BcEncoder.TInt32, T2 = BcEncoder.TVariable },
        new() { Kind = BcEncoder.OpRet, T1 = BcEncoder.TVariable },
        new() { Kind = BcEncoder.OpExit, T1 = BcEncoder.TInt32 },
    };

    static bool InstallOne(string name, int nativeIndex, bool withArg, Func<string, int>? registryIndexOf,
        Func<int, ulong> slot)
    {
        if (installed.Contains(name)) return true;
        if (nativeIndex < 0) return Fail($"trampoline {name}: native not registered");
        // #17 外扫实证（nodescan 普查，mslRoot 67/67 无节点）：运行时 exec 节点按「绑定」创建
        // （SCPT/FUNC/事件/GlobalInit 指向谁谁才有节点），不按 Code 条目——wrapper 根刻意
        // 不入 GlobalInit 且无人指向 → 0 节点；SCPT/FUNC 都指向子 → 索引只有 gml_Script_+名
        // 一条（StartOff=4，record len=68=整 buffer，BufPtr=共享 buffer 基址——dummy 头检查
        // 在子 record 上照旧成立）。直名 miss 回退子名把住 buffer。
        if (!NodeIndex.TryGet(name, out var node) && !NodeIndex.TryGet("gml_Script_" + name, out node))
            return Fail($"trampoline {name}: stub node not found");

        var translator = new Translator(new OpMsg { Entry = name }, registryIndexOf: registryIndexOf);
        byte[] head = BcEncoder.Encode(WrapperHead.ToList(), translator);
        byte[] dummy = Mem.ReadBytes(node.BufPtr, (int)node.BufLen);
        if (dummy.Length != 68 || !dummy.AsSpan(0, head.Length).SequenceEqual(head))
            return Fail($"trampoline {name}: dummy live buffer ({dummy.Length}B) != wrapper shape (68B, head {head.Length}B)");

        var sems = new List<SemInstruction>
        {
            // 前置 exit：根入口 0 的安全 no-op；子入口 4 起才是 trampoline 体
            new SemInstruction { Kind = BcEncoder.OpExit, T1 = BcEncoder.TInt32 },
        };
        if (withArg)
            sems.Add(new SemInstruction
            {
                Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable,
                // #20 定案：argument0 读 = Arg 域 -15（字 F1 FF 05 C0）——真源 = 同编译管线
                // TW 脚本逐字节实证（argdump）+ runtime handler Arg 分支（argIndex=id−93，
                // operand 0x5D=builtin 93 不变）。曾被误读 vanilla 扫描改成 -9（Stacktop，
                // F7 FF）——trampoline 帧无 with 栈，实例搜索落空抛 "Unable to find
                // instance for object index 0" → 错误格式化器撞帧断言 int3（#20 崩溃链）。
                Inst = -15 /* InstanceType.Arg */,
                Var = "argument0", RefTop = 0xA0,
            });
        sems.Add(new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = (ushort)(withArg ? 1 : 0), Fn = name });
        sems.Add(new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable });
        sems.AddRange(ReturnZeroTail);
        byte[] code = BcEncoder.Encode(sems, translator);

        // Translator 经 Registry.IndexOf(name) 解析——与注册时捕获的索引必须一致。
        // exit 前缀后 call 操作数位置：apply（无参）@8；report（多 8B 的 push.v argument0）@16
        uint encodedIdx = BitConverter.ToUInt32(code, (withArg ? 8 : 0) + 8);
        if (encodedIdx != (uint)nativeIndex)
            return Fail($"trampoline {name}: encoded call target {encodedIdx} != registered native {nativeIndex}");

        // #19 线程化代码 VM：派发走 record+0x20 handler 表（首执行按 buffer 懒构建），buffer
        // 仅供操作数——换 buffer 必须连表/pcmap 一起换，否则旧表把新字节当操作数静默空跑。
        byte[] table, pcmap;
        try { (table, pcmap) = TableBuilder.Build(code, slot); }
        catch (TranslationRejectException ex) { return Fail($"trampoline {name}: {ex.Message}"); }

        // 共享旧 buffer 的全部执行记录一起换（S2④：根+子双记录共享 BufPtr——两条都写）
        var sharing = NodeIndex.All.Where(n => n.BufPtr == node.BufPtr).ToList();
        // #19 真机自证（fail-closed）：任一共享记录已特化（+0x20≠0）就用构建器对旧 buffer
        // 重算、与活表逐字节比对——不等 = 特化规则复刻错了，装上即野派发，拒装
        foreach (var n in sharing)
            if (!TableBuilder.SelfProofRecord(n.Record, slot, out var why))
                return Fail($"trampoline {name}: specialization self-proof: {why}");

        ulong newBuf = Mem.AllocRW(code);
        ulong newTable = Mem.AllocRW(table);
        ulong newMap = Mem.AllocRW(pcmap);
        if (newBuf == 0 || newTable == 0 || newMap == 0) return Fail($"trampoline {name}: VirtualAlloc failed");
        foreach (var n in sharing)
            TableBuilder.WriteRecord(n.Record, (uint)code.Length, newBuf, newTable, newMap);
        installed.Add(name);
        AgentState.Log($"trampoline installed: {name} ({code.Length}B -> native #{nativeIndex}, buf 0x{newBuf:X}, table 0x{newTable:X})");
        return true;
    }

    static bool Fail(string why)
    {
        LastError = why;
        AgentState.Fail(why);
        return false;
    }
}
