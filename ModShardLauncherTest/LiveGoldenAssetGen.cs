using System.Text.Json;
using ModShardLauncher.HotReload;
using MslLive.Shared;
using UndertaleModLib;
using Xunit;
using Xunit.Abstractions;

namespace ModShardLauncherTest;

/// <summary>黄金资产生成器——<b>是工具不是测试</b>：仅当环境变量 MSL_GEN_LIVE_ASSETS=1 时执行，
/// 其余情况立即返回（空转通过）。从游戏当前 data.win（热加载目标进程实际引导的那份）
/// 提取 gml_GlobalScript_scr_unitRenderDrawSprite 的 SwapCode 载荷，写入
/// MslLive.Test/Assets/payload_unitRenderDrawSprite.json。
/// 游戏更新 / RefsExtractor 载荷格式变更后需重跑本生成器再跑 MslLive.Test 黄金测试。
/// 产物正确性由两侧共同钉死：文件形态编码必须逐字节等于 S2 sigdump（file 侧 bin），
/// 运行时形态编码必须逐字节等于 S2 活体 dump（712B golden）。</summary>
public class LiveGoldenAssetGen
{
    public const string EntryName = "gml_GlobalScript_scr_unitRenderDrawSprite";

    [Fact]
    public void Generate_unitRenderDrawSprite_payload()
    {
        if (Environment.GetEnvironmentVariable("MSL_GEN_LIVE_ASSETS") != "1") return;

        UndertaleData data;
        using (var fs = new FileStream(TestData.GameDataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            data = UndertaleIO.Read(fs, w => { });

        var code = data.Code.First(c => c.Name.Content == EntryName);
        var payload = RefsExtractor.Extract(code, data, AssetRanges.Empty, new AssetKindResolver());
        Assert.Equal(110, payload.Instructions.Count);   // S2 实测：父 buffer = 110 条指令

        // 内容 → STRG 索引（S2 ③：运行时 stringId == STRG chunk 索引 1:1）
        var strg = new Dictionary<string, int>();
        for (int i = 0; i < data.Strings.Count; i++)
            strg.TryAdd(data.Strings[i].Content, i);

        var op = new OpMsg
        {
            Seq = 0, Kind = "swap", Entry = EntryName,
            LocalsCount = (int)code.LocalsCount,
            ArgCount = 0,   // wrapper 节点 argc=0（S2 ① 实测）
            Instructions = payload.Instructions,
            Variables = payload.Variables,
            Functions = payload.Functions,
        };
        foreach (var s in payload.Strings)
            op.Strings.Add(new StrRef { Content = s, StrgIndex = strg.GetValueOrDefault(s, -1) });
        Assert.DoesNotContain(op.Strings, s => s.StrgIndex < 0);
        Assert.Empty(op.Assets);   // baseline entry：任何资产标注都意味着数据漂移

        string repoRoot = FindRepoRoot();
        string outDir = Path.Combine(repoRoot, "MslLive.Test", "Assets");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "payload_unitRenderDrawSprite.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(op));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ModShardLauncher.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (ModShardLauncher.sln) not found above " + AppContext.BaseDirectory);
    }
}
