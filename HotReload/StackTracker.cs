using System;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib.Models;

namespace ModShardLauncher.HotReload;

/// <summary>保守 VM 栈模拟：把消费者映射到它的 producer（指令下标）。
/// 未知/毒化槽位是 null。join point 只抹身份不抹深度（VM 要求基本块边界栈深一致）。</summary>
public sealed class StackTracker
{
    readonly List<int?> stack = new();
    readonly HashSet<uint> joinPoints = new();

    public StackTracker(IReadOnlyList<UndertaleInstruction> instructions)
    {
        foreach (var i in instructions)
        {
            if (i.Kind is UndertaleInstruction.Opcode.B or UndertaleInstruction.Opcode.Bt
                    or UndertaleInstruction.Opcode.Bf
                && !i.JumpOffsetPopenvExitMagic)
                joinPoints.Add((uint)((int)i.Address + i.JumpOffset));
            if (i.Kind == UndertaleInstruction.Opcode.Break && !i.JumpOffsetPopenvExitMagic)
                joinPoints.Add((uint)((int)i.Address + i.JumpOffset));
        }
    }

    /// <summary>读取 producer / 应用效果之前调用：当前指令是 join point 时毒化整个栈的身份。</summary>
    public void Before(UndertaleInstruction inst)
    {
        if (joinPoints.Contains(inst.Address)) PoisonAll();
    }

    /// <summary>栈顶 count 个槽位的 producer 下标，bottom-first（arg0 在前）。null = 未知。</summary>
    public IReadOnlyList<int?> Top(int count)
    {
        if (count <= 0) return Array.Empty<int?>();
        if (count > stack.Count)
            return Enumerable.Repeat<int?>(null, count).ToArray();
        return stack.GetRange(stack.Count - count, count);
    }

    /// <summary>应用栈效果。Push 类指令压入「自己的下标」作为 producer 身份；Conv/算术结果身份未知压 null。</summary>
    public void Apply(UndertaleInstruction inst, int index)
    {
        switch (inst.Kind)
        {
            case UndertaleInstruction.Opcode.Push:
            case UndertaleInstruction.Opcode.PushLoc:
            case UndertaleInstruction.Opcode.PushGlb:
            case UndertaleInstruction.Opcode.PushBltn:
            case UndertaleInstruction.Opcode.PushI:
                Push(index); // producer = 当前指令下标
                break;
            case UndertaleInstruction.Opcode.Pop:
                PopN(1);
                break;
            case UndertaleInstruction.Opcode.Call:
                PopN(inst.ArgumentsCount);
                break;
            case UndertaleInstruction.Opcode.CallV:
                PopN(2); Push(null);
                break;
            case UndertaleInstruction.Opcode.Conv:
            {
                // conv 不改变值的身份（conv.i.v 包字面量是编译器常态——draw_sprite(s_foo,…) 实参皆如此）。
                // 透传 producer 下标；TryAnnotate 只对真正的字面量指令动作，非字面量经 LiteralOf 自然落空。
                int? c = PopN(1)[0];
                Push(c);
                break;
            }
            case UndertaleInstruction.Opcode.Neg:
            case UndertaleInstruction.Opcode.Not:
                PopN(1); Push(null);
                break;
            case UndertaleInstruction.Opcode.Mul: case UndertaleInstruction.Opcode.Div:
            case UndertaleInstruction.Opcode.Rem: case UndertaleInstruction.Opcode.Mod:
            case UndertaleInstruction.Opcode.Add: case UndertaleInstruction.Opcode.Sub:
            case UndertaleInstruction.Opcode.And: case UndertaleInstruction.Opcode.Or:
            case UndertaleInstruction.Opcode.Xor: case UndertaleInstruction.Opcode.Shl:
            case UndertaleInstruction.Opcode.Shr: case UndertaleInstruction.Opcode.Cmp:
                PopN(2); Push(null);
                break;
            case UndertaleInstruction.Opcode.Dup:
                int? d = PopN(1)[0];
                Push(d); Push(d);
                break;
            case UndertaleInstruction.Opcode.Bt:
            case UndertaleInstruction.Opcode.Bf:
            case UndertaleInstruction.Opcode.PushEnv:
            case UndertaleInstruction.Opcode.PopEnv:
            case UndertaleInstruction.Opcode.Popz:
                PopN(1);
                break;
            case UndertaleInstruction.Opcode.Ret:
            case UndertaleInstruction.Opcode.Exit:
            case UndertaleInstruction.Opcode.Break:
                stack.Clear();
                break;
            default:
                break; // B 无栈效果；分支后的毒化统一在下方处理
        }
        if (inst.Kind == UndertaleInstruction.Opcode.B)
            PoisonAll(); // 无条件分支后的 fallthrough 视为新基本块
    }

    List<int?> PopN(int n)
    {
        var popped = new List<int?>();
        for (int i = 0; i < n; i++)
        {
            if (stack.Count == 0) popped.Add(null);
            else { popped.Add(stack[^1]); stack.RemoveAt(stack.Count - 1); }
        }
        return popped;
    }

    void Push(int? v) => stack.Add(v);
    void PoisonAll() { for (int i = 0; i < stack.Count; i++) stack[i] = null; }
}
