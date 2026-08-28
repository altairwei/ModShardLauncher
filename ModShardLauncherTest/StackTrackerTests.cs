using ModShardLauncher.HotReload;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

public class StackTrackerTests
{
    // vendored DLL 的 Address 只读——反射写底层字段给合成指令编地址
    static readonly System.Reflection.FieldInfo AddrField =
        typeof(UndertaleInstruction).GetFields(System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .First(f => f.Name.Contains("Address") && f.FieldType == typeof(uint));

    static UndertaleInstruction SetAddr(UndertaleInstruction i, uint a) { AddrField.SetValue(i, a); return i; }

    static UndertaleInstruction PushI(short v) => new()
    {
        Kind = UndertaleInstruction.Opcode.PushI,
        Type1 = UndertaleInstruction.DataType.Int16,
        Value = v,
    };

    static UndertaleInstruction Call(ushort argc, uint address = 0) => SetAddr(new()
    { Kind = UndertaleInstruction.Opcode.Call, ArgumentsCount = argc }, address);

    static UndertaleInstruction Pop() => new()
    { Kind = UndertaleInstruction.Opcode.Pop, Type1 = UndertaleInstruction.DataType.Variable };

    static UndertaleInstruction Bf(uint address, int jump) => SetAddr(new()
    { Kind = UndertaleInstruction.Opcode.Bf, JumpOffset = jump }, address);

    [Fact]
    public void Producers_OfCallArguments_AreBottomFirst()
    {
        var list = new List<UndertaleInstruction> { PushI(1), PushI(2), PushI(3), Call(3) };
        var t = new StackTracker(list);
        for (int i = 0; i < 3; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[3]);
        Assert.Equal(new int?[] { 0, 1, 2 }, t.Top(3));
    }

    [Fact]
    public void Pop_RemovesItsProducer()
    {
        var list = new List<UndertaleInstruction> { PushI(1), Pop(), PushI(2), Call(1) };
        var t = new StackTracker(list);
        for (int i = 0; i < 3; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[3]);
        Assert.Equal(new int?[] { 2 }, t.Top(1));
    }

    [Fact]
    public void JoinPoint_PoisonsProducerIdentity()
    {
        // 布局：PushI@0(4B) Bf@4(8B) PushI@12(4B) PushI@16(4B) Call@20
        // Bf 跳到 4+16=20 → Call 是 join point，两条路径的 producer 不同 → 毒化
        var list = new List<UndertaleInstruction>
        {
            PushI(1),
            Bf(4, 16),
            PushI(2),
            PushI(3),
            Call(2, address: 20),
        };
        var t = new StackTracker(list);
        for (int i = 0; i < 4; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[4]);
        Assert.Equal(new int?[] { null, null }, t.Top(2));
    }

    [Fact]
    public void AfterUnconditionalBranch_ProducersPoisoned()
    {
        var list = new List<UndertaleInstruction>
        {
            PushI(1),
            SetAddr(new UndertaleInstruction { Kind = UndertaleInstruction.Opcode.B, JumpOffset = 8 }, 4),
            PushI(2),
            Call(1, address: 12),
        };
        var t = new StackTracker(list);
        for (int i = 0; i < 3; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[3]);
        Assert.Equal(new int?[] { null }, t.Top(1));
    }

    [Fact]
    public void BinaryOp_ReplacesTwoProducersWithNull()
    {
        var list = new List<UndertaleInstruction>
        {
            PushI(1), PushI(2),
            new UndertaleInstruction { Kind = UndertaleInstruction.Opcode.Add },
            Call(1),
        };
        var t = new StackTracker(list);
        for (int i = 0; i < 3; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[3]);
        Assert.Equal(new int?[] { null }, t.Top(1));
    }

    [Fact]
    public void Dup_DuplicatesProducer()
    {
        var list = new List<UndertaleInstruction>
        {
            PushI(5),
            new UndertaleInstruction { Kind = UndertaleInstruction.Opcode.Dup },
            Call(2),
        };
        var t = new StackTracker(list);
        for (int i = 0; i < 2; i++) { t.Before(list[i]); t.Apply(list[i], i); }
        t.Before(list[2]);
        Assert.Equal(new int?[] { 0, 0 }, t.Top(2));
    }
}
