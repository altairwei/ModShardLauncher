using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>操作数解析器——Translator 实现它，测试用 ground-truth fake 实现它。
/// 全部解析返回 <b>low24 语义值</b>：变量 = 内置 smallId（raw，无偏置）或 100000+loadOrderId；
/// 调用 = 注册表原始索引（内置）或 100000+codeId（脚本）；字符串 = STRG 索引（== 运行时 stringId）；
/// 资产 = 运行时载体索引。变量操作数的顶字节不在此处——编码器从 sem.RefTop
/// （文件形态顶字节，静态占位期就已就位）原样回填。解析失败必须 throw
/// <see cref="TranslationRejectException"/>（fail-closed），禁止返回魔法值。</summary>
public interface IOperandResolver
{
    int ResolveString(StrRef s);
    int ResolveCall(string fn);
    uint ResolveVar(string name, short instType);
    int ResolveAsset(AssetRef a);
}

/// <summary>翻译层拒绝（非 boot 字符串 / 未映射变量 / 未知函数 / 未解析资产……）。
/// proof/apply 路径捕获后转成对应信封的失败语义，绝不写出半翻译 buffer。</summary>
public sealed class TranslationRejectException : Exception
{
    public TranslationRejectException(string reason) : base(reason) { }
}

/// <summary>bc17 编码器：SemInstruction 序列 → 运行时字节码 buffer。
/// 规格源 = UTMT UndertaleInstruction.Serialize（只读参考）+ S2 黄金 buffer 逐字节实测：
/// 指令字 u32 = opcode&lt;&lt;24 | T2&lt;&lt;20 | T1&lt;&lt;16 | low16（PushI 值 / Call argc /
/// Cmp 比较种在 b1 / Goto 24 位偏移带 23 位负数文件形态变换 / 其余 verbatim SwapExtra）。
/// 分支偏移 = 目标单元 − 本指令单元（4 字节为 1 单元，相对自身起点——S2 四点实测：
/// b +0xA6@0、bf +0x1B@0x0B→0x26、pushenv +160@4→164、popenv −159@164→5）。
/// 偏移随载荷 verbatim（sem.Jump），无需两遍回填。</summary>
public static class BcEncoder
{
    // opcode 值 = UTMT UndertaleInstruction.Opcode（bc17 现代形态）
    internal const byte
        OpConv = 0x07, OpMul = 0x08, OpDiv = 0x09, OpRem = 0x0A, OpMod = 0x0B,
        OpAdd = 0x0C, OpSub = 0x0D, OpAnd = 0x0E, OpOr = 0x0F, OpXor = 0x10,
        OpNeg = 0x11, OpNot = 0x12, OpShl = 0x13, OpShr = 0x14, OpCmp = 0x15,
        OpPop = 0x45, OpPushI = 0x84, OpDup = 0x86,
        OpCallV = 0x99, OpRet = 0x9C, OpExit = 0x9D, OpPopz = 0x9E,
        OpB = 0xB6, OpBt = 0xB7, OpBf = 0xB8, OpPushEnv = 0xBA, OpPopEnv = 0xBB,
        OpPush = 0xC0, OpPushLoc = 0xC1, OpPushGlb = 0xC2, OpPushBltn = 0xC3,
        OpCall = 0xD9, OpBreak = 0xFF;

    // DataType 值 = UTMT UndertaleInstruction.DataType
    internal const byte
        TDouble = 0, TFloat = 1, TInt32 = 2, TInt64 = 3, TBoolean = 4,
        TVariable = 5, TString = 6, TInt16 = 0x0F;

    internal enum Cat { Single, Double, Cmp, Goto, Pop, Push, Call, Break }

    internal static Cat Category(byte kind) => kind switch
    {
        OpNeg or OpNot or OpDup or OpRet or OpExit or OpPopz or OpCallV => Cat.Single,
        OpConv or OpMul or OpDiv or OpRem or OpMod or OpAdd or OpSub
            or OpAnd or OpOr or OpXor or OpShl or OpShr => Cat.Double,
        OpCmp => Cat.Cmp,
        OpB or OpBt or OpBf or OpPushEnv or OpPopEnv => Cat.Goto,
        OpPop => Cat.Pop,
        OpPush or OpPushLoc or OpPushGlb or OpPushBltn or OpPushI => Cat.Push,
        OpCall => Cat.Call,
        OpBreak => Cat.Break,
        _ => throw new TranslationRejectException($"unknown opcode 0x{kind:X2}"),
    };

    /// <summary>指令字节尺寸（编码器与 VarCalibrator 的读回走查共用同一张表——
    /// 尺寸表若错，两侧同错，黄金测试字节比对负责抓）。</summary>
    public static int ByteSize(SemInstruction sem) => Category(sem.Kind) switch
    {
        Cat.Single or Cat.Double or Cat.Cmp or Cat.Goto => 4,
        Cat.Pop => sem.T1 == TInt16 ? 4 : 8,
        Cat.Push => sem.T1 switch
        {
            TInt16 => 4,
            TInt32 or TVariable or TString => 8,
            TDouble or TInt64 => 12,
            _ => throw new TranslationRejectException($"push type {sem.T1} has no operand encoding"),
        },
        Cat.Call => 8,
        Cat.Break => sem.T1 == TInt32 ? 8 : 4,
        _ => 4,
    };

    public static byte[] Encode(List<SemInstruction> insts, IOperandResolver resolver)
    {
        // 容量预估：大多 4/8 字节
        var buf = new List<byte>(insts.Count * 8);
        foreach (var sem in insts)
        {
            uint w = (uint)sem.Kind << 24 | (uint)(sem.T2 & 0xF) << 20 | (uint)(sem.T1 & 0xF) << 16;
            switch (Category(sem.Kind))
            {
                case Cat.Single:
                case Cat.Double:
                case Cat.Break:
                    w |= sem.Low16;   // verbatim（SwapExtra/ExtendedKind；Break 操作数在下方）
                    break;
                case Cat.Cmp:
                    w |= (uint)(sem.Cmp ?? throw new TranslationRejectException("cmp without comparison kind")) << 8;
                    break;
                case Cat.Goto:
                {
                    int j = sem.Jump ?? throw new TranslationRejectException($"{OpName(sem.Kind)} without jump offset");
                    uint low24 = (uint)j & 0xFFFFFF;
                    // UTMT Serialize 同款：24 位负数 → 23 位负数文件形态（popenv 退出 magic 0xF00000 豁免）
                    if (low24 != 0xF00000 && (low24 & 0x800000) != 0)
                        low24 = (low24 & ~0x800000u) | 0x400000;
                    w |= low24;
                    break;
                }
                case Cat.Pop:
                case Cat.Push when sem.Kind != OpPushI && sem.T1 != TInt16:
                    w |= (ushort)sem.Inst;   // TypeInst 位（局部 -7 / 全局 -5 / self -1 / arg -15 / stacktop 0…）
                    break;
                case Cat.Push:   // pushi.e 与 push.e（T1=Int16）字面量：值在 low16（fix-loop #14 真机证明
                                  // push.e 形态存在——布尔物化舞步；新资产字面量经标注后也要翻译 low16）
                    if (sem.AssetKinds is { Count: > 0 })
                    {
                        int rt = ResolveAsset(sem, resolver);
                        if (rt != (short)rt)
                            throw new TranslationRejectException($"asset runtime index {rt} overflows push i16 (pushi.e/push.e)");
                        w |= (ushort)(short)rt;
                    }
                    else w |= sem.Low16;
                    break;
                case Cat.Call:
                    w |= sem.Low16;   // argc
                    break;
            }
            WriteU32(buf, w);

            switch (Category(sem.Kind))
            {
                case Cat.Pop when sem.T1 != TInt16:
                    WriteU32(buf, VarOperand(sem, resolver));
                    break;
                case Cat.Push:
                    switch (sem.T1)
                    {
                        case TInt16: break;   // 值已在指令字
                        case TInt32:
                            if (sem.Fn != null) WriteU32(buf, (uint)resolver.ResolveCall(sem.Fn));
                            else if (sem.AssetKinds is { Count: > 0 }) WriteU32(buf, (uint)ResolveAsset(sem, resolver));
                            else if (sem.Int != null) WriteU32(buf, (uint)(int)sem.Int.Value);
                            else throw new TranslationRejectException("push.i without value");
                            break;
                        case TInt64:
                            if (sem.Int == null) throw new TranslationRejectException("push.l without value");
                            WriteU64(buf, (ulong)sem.Int.Value);
                            break;
                        case TDouble:
                            if (sem.Real == null) throw new TranslationRejectException("push.d without value");
                            WriteU64(buf, (ulong)BitConverter.DoubleToInt64Bits(sem.Real.Value));
                            break;
                        case TVariable:
                            WriteU32(buf, VarOperand(sem, resolver));
                            break;
                        case TString:
                            if (sem.Str == null) throw new TranslationRejectException("push.s without string");
                            WriteU32(buf, (uint)resolver.ResolveString(new StrRef { Content = sem.Str }));
                            break;
                        default:
                            throw new TranslationRejectException($"push type {sem.T1} has no operand encoding");
                    }
                    break;
                case Cat.Call:
                    if (sem.Fn == null) throw new TranslationRejectException("call without function name");
                    WriteU32(buf, (uint)resolver.ResolveCall(sem.Fn));
                    break;
                case Cat.Break when sem.T1 == TInt32:
                    if (sem.Fn != null) throw new TranslationRejectException("break function ref unsupported");
                    WriteU32(buf, (uint)(int)(sem.Int ?? throw new TranslationRejectException("break.i without argument")));
                    break;
            }
        }
        return buf.ToArray();
    }

    /// <summary>变量引用操作数：顶字节 = 文件形态原样（sem.RefTop，0xA0/0x80），
    /// low24 = 解析值（内置 smallId raw / 100000+符号id）。统一模型见 findings-t11 新发现 4。</summary>
    static uint VarOperand(SemInstruction sem, IOperandResolver resolver)
    {
        if (sem.Var == null) throw new TranslationRejectException($"{OpName(sem.Kind)} variable operand without name");
        uint low = resolver.ResolveVar(sem.Var, sem.Inst);
        if (low >= 0x1000000) throw new TranslationRejectException($"var '{sem.Var}' resolved out of low24: {low}");
        return (uint)sem.RefTop << 24 | low;
    }

    static int ResolveAsset(SemInstruction sem, IOperandResolver resolver)
    {
        long idx = sem.Int ?? throw new TranslationRejectException("annotated asset literal without value");
        return resolver.ResolveAsset(new AssetRef { Kind = sem.AssetKinds![0], Index = idx });
    }

    static string OpName(byte kind) => $"0x{kind:X2}";

    static void WriteU32(List<byte> buf, uint v)
    {
        buf.Add((byte)v); buf.Add((byte)(v >> 8)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 24));
    }

    static void WriteU64(List<byte> buf, ulong v)
    {
        for (int i = 0; i < 8; i++) buf.Add((byte)(v >> (8 * i)));
    }
}
