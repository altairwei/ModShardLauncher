using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>把 dummy（"return 0;"）stub 换成 trampoline：[push.v argument0（仅 report）]
/// + call.v 原生索引 + popz.v（丢返回值）+ return 0 收尾。
/// 收尾形态由 ModShardLauncherTest.StubDummy_CompilesTo_PushiZeroRetV 钉版
/// （pushi.e 0 + conv.i.v + ret.v = 12 字节）；安装前要求 dummy 活 buffer 与编码收尾
/// <b>全等</b>——不等 = 编译器形态假设错了，fail-closed 不安装。
/// call 操作数经 Translator 走 Registry.IndexOf（原生注册后的真实槽位），并与
/// NativeRegistration 捕获的索引交叉断言。旧 buffer 永不释放（S3 旧帧安全）。
/// 幂等：进程内已安装的 stub 不重复安装（proof 重连重发不会把 trampoline 当 dummy 判死）。</summary>
public static class Trampoline
{
    public const string ApplyName = "msl_live_apply";
    public const string ReportName = "msl_live_report";

    static readonly HashSet<string> installed = new();

    /// <summary>最近一次失败的原因（PipeServer 拼装 proofAck.Error 用）。</summary>
    public static string LastError { get; private set; } = "";

    internal static void ResetForTest() { installed.Clear(); LastError = ""; }

    public static bool Install(Func<string, int>? registryIndexOf = null)
    {
        bool ok = InstallOne(ApplyName, NativeRegistration.ApplyIndex, withArg: false, registryIndexOf);
        ok &= InstallOne(ReportName, NativeRegistration.ReportIndex, withArg: true, registryIndexOf);
        return ok;
    }

    /// <summary>return-0 收尾的语义指令（也是 dummy 本体的完整内容）。</summary>
    internal static readonly SemInstruction[] ReturnZeroTail =
    {
        new() { Kind = BcEncoder.OpPushI, T1 = BcEncoder.TInt16, Low16 = 0 },          // pushi.e 0
        new() { Kind = BcEncoder.OpConv, T1 = BcEncoder.TInt32, T2 = BcEncoder.TVariable }, // conv.i.v
        new() { Kind = BcEncoder.OpRet, T1 = BcEncoder.TVariable },                    // ret.v
    };

    static bool InstallOne(string name, int nativeIndex, bool withArg, Func<string, int>? registryIndexOf)
    {
        if (installed.Contains(name)) return true;
        if (nativeIndex < 0) return Fail($"trampoline {name}: native not registered");
        if (!NodeIndex.TryGet(name, out var node)) return Fail($"trampoline {name}: stub node not found");

        var translator = new Translator(new OpMsg { Entry = name }, registryIndexOf: registryIndexOf);
        byte[] tail = BcEncoder.Encode(ReturnZeroTail.ToList(), translator);
        byte[] dummy = Mem.ReadBytes(node.BufPtr, (int)node.BufLen);
        if (!dummy.SequenceEqual(tail))
            return Fail($"trampoline {name}: dummy live buffer ({dummy.Length}B) != return-0 shape ({tail.Length}B)");

        var sems = new List<SemInstruction>();
        if (withArg)
            sems.Add(new SemInstruction
            {
                Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable,
                Inst = -15 /* InstanceType.Arg */, Var = "argument0", RefTop = 0xA0,
            });
        sems.Add(new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = (ushort)(withArg ? 1 : 0), Fn = name });
        sems.Add(new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable });
        sems.AddRange(ReturnZeroTail);
        byte[] code = BcEncoder.Encode(sems, translator);

        // Translator 经 Registry.IndexOf(name) 解析——与注册时捕获的索引必须一致
        uint encodedIdx = BitConverter.ToUInt32(code, (withArg ? 8 : 0) + 4);
        if (encodedIdx != (uint)nativeIndex)
            return Fail($"trampoline {name}: encoded call target {encodedIdx} != registered native {nativeIndex}");

        ulong newBuf = Mem.AllocRW(code);
        if (newBuf == 0) return Fail($"trampoline {name}: VirtualAlloc failed");
        // 共享旧 buffer 的全部执行记录一起换（stub 无子别名，正常只命中一条）
        foreach (var n in NodeIndex.All.Where(n => n.BufPtr == node.BufPtr))
        {
            Mem.WriteU32(n.Record + 0x08, (uint)code.Length);
            Mem.WriteU64(n.Record + 0x18, newBuf);
        }
        installed.Add(name);
        AgentState.Log($"trampoline installed: {name} ({code.Length}B -> native #{nativeIndex}, buf 0x{newBuf:X})");
        return true;
    }

    static bool Fail(string why)
    {
        LastError = why;
        AgentState.Fail(why);
        return false;
    }
}
