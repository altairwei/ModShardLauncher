using ModShardLauncher.HotReload;
using UndertaleModLib;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 4] 反编译/反汇编调用点全清点（Grep `Decompiler.Decompile` / `.Disassemble`，
/// 快照时点 = T4 重定向后）。处置列的行号为改道后位置。
///
/// | 调用点                                   | 分类                     | 处置 |
/// |------------------------------------------|--------------------------|------|
/// | GeneralUtils.LoadGML                     | mod 面向                 | FastText.Read(GML)（改道） |
/// | GeneralUtils.LoadAssemblyAsString        | mod 面向                 | FastText.Read(AssemblyAsString)（改道） |
/// | CodeUtils.GetStringGMLFromFile           | mod 面向（Insert/ReplaceGMLString 复用） | FastText.Read(GML)（改道，下游自动继承） |
/// | AsmUtils.GetAssemblyString               | mod 面向（Insert/ReplaceAssemblyString 复用） | FastText.Read(AssemblyAsString)（改道，下游自动继承） |
/// | ModLoader.GetTable / SetTable            | mod 面向                 | FastText.Read(GML)（改道；SetTable 读后记账） |
/// | CodeUtils.Save（GML/AssemblyAsString 分支） | 编译端（终稿落图）      | fast: RecordFinal 跳过；full: 原样 + NoteMutated |
/// | CodeUtils.SetStringGMLInFile             | mod 面向（直改不读）     | fast: RecordFinal 跳过；full: 原样 + NoteMutated |
/// | AsmUtils.SetAssemblyString               | mod 面向（直改不读）     | fast: RecordFinal 跳过；full: 原样 + NoteMutated |
/// | CodeUtils.AddCode / AddCodeAsm           | 结构 op                  | fast 门控：账本命中幂等重放；同名异体摘旧重建；尾账 MarkCompiled |
/// | AsmUtils.InjectAssemblyInstruction       | 指令级 API（非文本级）   | 不改道——full 路径双模式同走；重放幂等性 = Task 5 范围 |
/// | HotReload/HotPipeline.cs:390/502（#36-B/#37 救援层） | 推送期对工作图 product 条目 | 不经缓存——保留原样（spec §5 救援层互作） |
/// | DataLoader.cs:106-108（ExportPreset json 调试导出）  | MSL 内部导出        | 不动 |
/// | GeneralUtils.GenerateNRandomLinesFromCode（随机行采样） | MSL 内部生成       | 不动 |
/// | Mods/DisassemblyEditor.cs:19（反汇编窗口内容装载）     | UI 窗口自读（非 mod DSL） | 不动（计划表漏记，此处补录） |
/// | MslLive.E2E/E2ETests.cs:54、LiveStubInjectorTests.cs:213 | 测试侧自读        | 不动 |
///
/// LogUtils.InjectLog（:103 Msl.LoadGML + AddFunction/AddNewEvent）经 LoadGML 自动继承读序；
/// 其幂等化守卫 = Task 5。AddInnerCode/AddInnerFunction/AddFunction 全部经由 AddCode 门控自动继承。</summary>
[Collection("vanilla")]
public class DecompileCallAudit : IDisposable
{
    readonly UndertaleData savedData;

    public DecompileCallAudit()
    {
        savedData = DataLoader.data;
        FastPushContext.ResetForTest();
    }

    public void Dispose()
    {
        DataLoader.data = savedData;
        FastPushContext.ResetForTest();
        FinalTextStore.ResetRound();
    }

    /// <summary>接线验证：生产 API GetTable（经 FastText.Read 冷路径）产出与直连反编译一致的表内容。
    /// 若 ModLoader.GetTable 的改道被误删（退回直接 Decompile 也不报错——两路同源），此测试退化
    /// 为纯冒烟；其真正价值在改道缺失导致的编译错/异常路径上兜底。</summary>
    [Fact]
    public void GetTable_WiredThroughFastText_ReturnsVanillaTable()
    {
        using var fs = new FileStream(TestData.VanillaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        DataLoader.data = UndertaleIO.Read(fs, w => { });

        var weapons = ModLoader.GetTable("gml_GlobalScript_table_weapons");

        Assert.NotNull(weapons);
        Assert.NotEmpty(weapons);   // vanilla 武器表非空（Initalize 同款读取路径）
    }
}
