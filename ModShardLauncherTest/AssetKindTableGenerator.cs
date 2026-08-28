using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace ModShardLauncherTest;

/// <summary>从 UTMT 源码树的 gamemaker.json 蒸馏出只含 Asset.* 条目的 HotReload/assetkinds.json。
/// 手工运行（UTMT 源树路径是机器相关的）：dotnet test --filter AssetKindTableGenerator
/// 产物必须提交进仓库；运行时只读内嵌资源，不再依赖源树。</summary>
public class AssetKindTableGenerator
{
    const string Source = @"E:\StoneShard_Mod_Data\tools\UndertaleModTool\UndertaleModLib\GameSpecificData\Underanalyzer\gamemaker.json";
    static string Dest => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HotReload", "assetkinds.json");

    readonly ITestOutputHelper o;
    public AssetKindTableGenerator(ITestOutputHelper o) => this.o = o;

    [Fact]
    public void Generate()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Source));
        JsonElement gn = doc.RootElement.GetProperty("GlobalNames");
        var variables = new SortedDictionary<string, string>();
        var functions = new SortedDictionary<string, string?[]>();
        foreach (var v in gn.GetProperty("Variables").EnumerateObject())
        {
            if (v.Value.ValueKind != JsonValueKind.String) continue;
            string? s = v.Value.GetString();
            if (s != null && s.StartsWith("Asset.")) variables[v.Name] = s["Asset.".Length..];
        }
        foreach (var f in gn.GetProperty("FunctionArguments").EnumerateObject())
        {
            if (f.Value.ValueKind == JsonValueKind.Array)
                Merge(f.Name, Slots(f.Value));
            else if (f.Value.ValueKind == JsonValueKind.Object
                     && f.Value.TryGetProperty("Macros", out var macros))
                foreach (var m in macros.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Array))
                    Merge(f.Name, Slots(m));

            void Merge(string name, string?[] slots)
            {
                if (slots.All(s => s == null)) return;
                if (!functions.TryGetValue(name, out var cur)) functions[name] = slots;
                else
                {
                    int max = Math.Max(cur.Length, slots.Length);
                    var merged = new string?[max];
                    for (int i = 0; i < max; i++)
                        merged[i] = (i < cur.Length ? cur[i] : null) ?? (i < slots.Length ? slots[i] : null);
                    functions[name] = merged;
                }
            }
        }
        static string?[] Slots(JsonElement arr) => arr.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String && e.GetString() is string s && s.StartsWith("Asset.")
                ? s["Asset.".Length..] : null)
            .ToArray();

        var payload = new { variables, functions };
        File.WriteAllText(Dest, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
        o.WriteLine($"wrote {Dest}: {variables.Count} variables, {functions.Count} functions");
        // 蒸馏量 sanity：Asset.* 变量实测恰 11 个（2026-08-28 对 gamemaker.json 逐条核验：sprite_index、
        // mask_index、object_index、room、room_first、room_last、event_object、view_object、
        // background_index、path_index、timeline_index——计划原稿 >50 是未核实的猜测，按实测钉死）。
        Assert.True(variables.Count == 11 && functions.Count > 200);
    }
}
