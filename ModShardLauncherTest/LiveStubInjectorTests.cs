using ModShardLauncher.HotReload;
using UndertaleModLib;
using UndertaleModLib.Models;
using Xunit;

namespace ModShardLauncherTest;

[Collection("vanilla")]
public class LiveStubInjectorTests : IDisposable
{
    readonly UndertaleData? savedData;

    public LiveStubInjectorTests() => savedData = DataLoader.data;
    public void Dispose() { if (savedData != null) DataLoader.data = savedData; }

    static UndertaleData Load()
    {
        UndertaleData data;
        using (var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });
        // Msl.* 原语硬绑定 ModLoader.Data（= DataLoader.data）——指到测试装载的 data
        DataLoader.data = data;
        return data;
    }

    [Fact]
    public void Inject_CreatesAllStructures()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);

        for (int i = 0; i < q.ScriptSlots; i++)
            Assert.Contains(data.Code, c => c.Name.Content == $"msl_slot_{i}");
        foreach (var n in new[] { "msl_live_apply", "msl_live_report", "msl_loader_0" })
            Assert.Contains(data.Code, c => c.Name.Content == n);

        // SCPT + GlobalInit 注册（Task 16 真机缺漏修复）：无 SCPT 条目 = VM load 时
        // "Unable to find function"。全部 stub（apply/report/loader/64 slots）都必须注册。
        foreach (var n in new[] { "msl_live_apply", "msl_live_report", "msl_loader_0" }
                 .Concat(Enumerable.Range(0, q.ScriptSlots).Select(i => $"msl_slot_{i}")))
        {
            var code = data.Code.First(c => c.Name.Content == n);
            Assert.Contains(data.Scripts, s => s.Name.Content == n && s.Code == code);
            Assert.Contains(data.GlobalInitScripts, g => g.Code == code);
        }

        var manager = data.GameObjects.First(o => o.Name.Content == "o_msl_live");
        Assert.True(manager.Persistent);
        Assert.Contains(manager.Events[(int)EventType.Step], e => e.EventSubtype == 0);
        Assert.Contains(manager.Events[(int)EventType.Other],
            e => e.EventSubtype == (uint)EventSubtypeOther.GameStart);

        // loader 路径拼接用字符串已播种（loader GML 只允许 boot 字符串）
        Assert.Contains(data.Strings, s => s.Content == "mods/_live/res/");
        Assert.Contains(data.Strings, s => s.Content == ".png");

        var start = data.Rooms.First(r => r.Name.Content == "START");
        Assert.Contains(start.GameObjects, g => g.ObjectDefinition?.Name?.Content == "o_msl_live");

        for (int i = 0; i < q.ShellObjects; i++)
        {
            var shell = data.GameObjects.First(o => o.Name.Content == $"o_msl_shell_{i}");
            if (q.ShellParents[i] != "")
                Assert.Equal(q.ShellParents[i], shell.ParentId?.Name?.Content);
            Assert.Contains(shell.Events[(int)EventType.Create], e => e.EventSubtype == 0);
            Assert.Contains(shell.Events[(int)EventType.Other], e => e.EventSubtype == 10);
        }

        for (int i = 0; i < q.EmptyRooms; i++)
            Assert.Contains(data.Rooms, r => r.Name.Content == $"r_msl_empty_{i}");
        Assert.True(q.RoomBaseIndex > 0);

        // step 事件的 msl_live_apply 调用必须真的解析到同名函数（编译期绑定检查）
        var stepCode = data.Code.First(c => c.Name.Content == LiveStubInjector.ManagerStepEntry);
        Assert.Contains(stepCode.Instructions,
            i => i.Function?.Target?.Name?.Content == "msl_live_apply");
    }

    /// <summary>dummy stub（"return 0;"）的编译形态钉版：Task 14 的 Trampoline 收尾字节校验
    /// 以它为基准（pushi.e 0 + conv.i.v + ret.v = 12 字节，探针实测）。编译器输出若变，本测试红 =
    /// MslLive.Test 的 Trampoline fixture 同步过期。</summary>
    [Fact]
    public void StubDummy_CompilesTo_PushiZeroRetV()
    {
        var data = Load();
        LiveStubInjector.Inject(data, new LiveQuotas());
        var code = data.Code.First(c => c.Name.Content == LiveStubInjector.ApplyFn);
        // 实测形态（vendored dll 编译器，探针读出）：pushi.e 0 → conv.i.v → ret.v
        // = 12 字节 00 00 0F 84 | 00 00 52 07 | 00 00 05 9C
        Assert.Equal(3, code.Instructions.Count);
        var push = code.Instructions[0];
        Assert.Equal(UndertaleInstruction.Opcode.PushI, push.Kind);
        Assert.Equal((short)0, Assert.IsType<short>(push.Value));
        var conv = code.Instructions[1];
        Assert.Equal(UndertaleInstruction.Opcode.Conv, conv.Kind);
        Assert.Equal(UndertaleInstruction.DataType.Int32, conv.Type1);
        Assert.Equal(UndertaleInstruction.DataType.Variable, conv.Type2);
        var ret = code.Instructions[2];
        Assert.Equal(UndertaleInstruction.Opcode.Ret, ret.Kind);
        Assert.Equal(UndertaleInstruction.DataType.Variable, ret.Type1);
    }

    [Fact]
    public void Inject_Twice_IsIdempotent()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);
        int codeCount = data.Code.Count, objCount = data.GameObjects.Count, roomCount = data.Rooms.Count;
        int scptCount = data.Scripts.Count, initCount = data.GlobalInitScripts.Count;
        LiveStubInjector.Inject(data, q);
        Assert.Equal(codeCount, data.Code.Count);
        Assert.Equal(objCount, data.GameObjects.Count);
        Assert.Equal(roomCount, data.Rooms.Count);
        Assert.Equal(scptCount, data.Scripts.Count);
        Assert.Equal(initCount, data.GlobalInitScripts.Count);
    }

    /// <summary>自愈路径：在「有 stub 但无 SCPT/GlobalInit 注册」的历史产物上重跑 Inject，
    /// 注册必须补齐且 code 不重复（Task 16 之前编译的 data.win 重编场景）。</summary>
    [Fact]
    public void Inject_HealsLegacyStubWithoutRegistration()
    {
        var data = Load();
        Msl.AddFunction("return 0;", LiveStubInjector.ApplyFn);   // 模拟旧产物：只有 code entry
        LiveStubInjector.Inject(data, new LiveQuotas());
        Assert.Single(data.Code.Where(c => c.Name.Content == LiveStubInjector.ApplyFn));   // 不重复
        var code = data.Code.First(c => c.Name.Content == LiveStubInjector.ApplyFn);
        Assert.Contains(data.Scripts, s => s.Name.Content == LiveStubInjector.ApplyFn && s.Code == code);
        Assert.Contains(data.GlobalInitScripts, g => g.Code == code);
    }
}
