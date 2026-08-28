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

    [Fact]
    public void Inject_Twice_IsIdempotent()
    {
        var data = Load();
        var q = new LiveQuotas();
        LiveStubInjector.Inject(data, q);
        int codeCount = data.Code.Count, objCount = data.GameObjects.Count, roomCount = data.Rooms.Count;
        LiveStubInjector.Inject(data, q);
        Assert.Equal(codeCount, data.Code.Count);
        Assert.Equal(objCount, data.GameObjects.Count);
        Assert.Equal(roomCount, data.Rooms.Count);
    }
}
