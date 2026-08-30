using ModShardLauncher;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>AddFunction/AddCode 的子 wrapper 布局回归（Task 16 fix-loop #5，真机 splash
/// 后闪退 + UTMT 读不回 data.win 的根因）：GMS2.3 编译含 function 声明的 GML 时会生成
/// gml_Script_* 子 wrapper。子条目不序列化 ParentEntry，读侧按“字节码地址与父共享”
/// 推断归属；写入器给 child 编址用的是全局游标（最近写完 blob 的 root）——因此 child
/// 在 Code 列表里必须紧跟其父。AddCode 旧序（ReplaceGML 在 Code.Add 之前）让编译器
/// 面对一个尚未入列的根（IndexOf = -1），子 wrapper 被插到列表头：保存后拿到退化地址
/// 4（= FORM size 字段）、len≈38MB，游戏把文件头当字节码执行即闪退；读侧同址推断
/// 失效导致 UTMT 读取 NRE。</summary>
[Collection("vanilla")]
public class CodeUtilsAddFunctionTests : IDisposable
{
    readonly UndertaleData? savedData;

    public CodeUtilsAddFunctionTests() => savedData = DataLoader.data;
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
    public void AddFunction_PlacesChildWrapper_AfterRoot_AndSurvivesRoundTrip()
    {
        var data = Load();
        string headBefore = data.Code[0].Name.Content;

        Msl.AddFunction("function msl_test_fn(a, b) { return a + b; }", "msl_test_fn");

        // 模型层：子 wrapper 必须紧跟其根（写入器编址的全局游标语义要求的布局）
        var root = data.Code.First(c => c.Name.Content == "msl_test_fn");
        var child = data.Code.FirstOrDefault(c => c.Name.Content == "gml_Script_msl_test_fn");
        Assert.NotNull(child);
        int rootIdx = data.Code.IndexOf(root);
        int childIdx = data.Code.IndexOf(child);
        Assert.True(childIdx == rootIdx + 1,
            $"gml_Script_msl_test_fn 在 [{childIdx}]，根 msl_test_fn 在 [{rootIdx}]；子 wrapper 必须紧跟其父");
        // 不许动列表头部的 vanilla 条目（旧轮转的教训）
        Assert.Equal(headBefore, data.Code[0].Name.Content);

        // 落盘-读回（vendored UndertaleIO.Read 在 MemoryStream 上 NRE，必须走磁盘文件）
        UndertaleData reloaded;
        string tmp = Path.Combine(Path.GetTempPath(), "msl-addfn-" + Guid.NewGuid().ToString("N") + ".win");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                UndertaleIO.Write(fs, data);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                reloaded = UndertaleIO.Read(fs, _ => { });
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        // 读侧按共享地址推断归属：child 应回到 root 名下，Length 一致（共享同一 blob）
        var rRoot = reloaded.Code.First(c => c.Name.Content == "msl_test_fn");
        var rChild = reloaded.Code.FirstOrDefault(c => c.Name.Content == "gml_Script_msl_test_fn");
        Assert.NotNull(rChild);
        Assert.Same(rRoot, rChild.ParentEntry);
        Assert.Equal(rRoot.Length, rChild.Length);
        // 不退化：编译产物是几十字节量级，不是 38MB 的“到下一个 blob 的跨度”
        Assert.True(rRoot.Length < 4096, $"退化 Length: {rRoot.Length}");
    }
}
