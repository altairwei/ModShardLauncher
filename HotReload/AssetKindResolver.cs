using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ModShardLauncher.HotReload;

/// <summary>封闭查表——UTMT 反编译器渲染资产名的同源机制（gamemaker.json 蒸馏副本内嵌）。
/// 内置 room 变量表：gamemaker.json 不覆盖 roomNext 等 StoneShard 内置 room 变量
/// （S4 拒绝分类①的修复，findings-s4-real.md）。</summary>
public sealed class AssetKindResolver
{
    static readonly Dictionary<string, AssetKind> BuiltinRoomVariables = new()
    {
        ["roomNext"] = AssetKind.Room,
        ["roomPrevious"] = AssetKind.Room,
    };

    readonly Dictionary<string, AssetKind> variableKinds = new();
    readonly Dictionary<string, AssetKind?[]> functionArgs = new();

    public AssetKindResolver()
    {
        var asm = Assembly.GetExecutingAssembly();
        string resName = asm.GetManifestResourceNames().First(n => n.EndsWith("assetkinds.json"));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var doc = JsonDocument.Parse(stream);
        foreach (var v in doc.RootElement.GetProperty("variables").EnumerateObject())
            if (Enum.TryParse<AssetKind>(v.Value.GetString(), out var k))
                variableKinds[v.Name] = k;
        foreach (var f in doc.RootElement.GetProperty("functions").EnumerateObject())
            functionArgs[f.Name] = f.Value.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String && Enum.TryParse<AssetKind>(e.GetString(), out var k) ? k : (AssetKind?)null)
                .ToArray();
    }

    /// <summary>赋值目标定类：sprite_index = &lt;literal&gt; → Sprite。内置 room 变量优先。</summary>
    public AssetKind? KindForVariable(string variableName)
    {
        if (BuiltinRoomVariables.TryGetValue(variableName, out var bk)) return bk;
        return variableKinds.TryGetValue(variableName, out var k) ? k : null;
    }

    /// <summary>调用参数定类：draw_sprite(&lt;literal&gt;,…) arg0 → Sprite。槽位越表或函数不在表内 → null。</summary>
    public AssetKind? KindForCallArg(string functionName, int slot)
    {
        if (!functionArgs.TryGetValue(functionName, out var slots)) return null;
        if (slot < 0 || slot >= slots.Length) return null;
        return slots[slot];
    }

    public AssetKind KindForPushEnv() => AssetKind.Object;

    /// <summary>内置变量判定（VarIdSimulator 用）：在 gamemaker.json 变量表或内置 room 变量表里的名字。
    /// 内置变量走 exe 固定 smallId 表（S2 ②），不占装载序 id 空间。</summary>
    public bool IsBuiltinVariable(string variableName) =>
        BuiltinRoomVariables.ContainsKey(variableName) || variableKinds.ContainsKey(variableName);
}
