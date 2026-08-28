using System.Reflection;
using System.Text.Json;

namespace MslLive.Shared;

/// <summary>内置变量名 → smallId 固定表（Task 11 实测 StoneShard.exe 注册序，共 218 条，ids 0..217 连续）。
/// 表来源 = exe 内置变量注册表（基址 0x1407EDAA0、步进 0x20、计数全局 0x1407F1920）；
/// 锚点：sprite_index=26、argument0=93、object_index=14（另 5 点交叉验证见 findings-t11.md）。
/// 序列化形态 = 扁平 name→id 字典，按 id 升序排列；agent 编码器与 MSL 模拟器共用本表。</summary>
public static class BuiltinVars
{
    const string ResourceName = "MslLive.Shared.BuiltinVars.json";

    static readonly Lazy<Dictionary<string, int>> LazyMap = new(LoadCore);

    /// <summary>完整表（只读快照——返回的字典请勿修改）。</summary>
    public static IReadOnlyDictionary<string, int> Map => LazyMap.Value;

    /// <summary>查 id；表外名字返回 false（调用方 fail-closed）。</summary>
    public static bool TryGetId(string name, out int id) => LazyMap.Value.TryGetValue(name, out id);

    static Dictionary<string, int> LoadCore()
    {
        var asm = typeof(BuiltinVars).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} missing");
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new InvalidOperationException($"{ResourceName} deserialized to null");
        if (map.Count == 0) throw new InvalidOperationException($"{ResourceName} is empty");
        return map;
    }
}
