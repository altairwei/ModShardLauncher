using System.Collections.Generic;
using MslLive.Shared;

namespace ModShardLauncher.HotReload;

/// <summary>提取结果容器（HotReload 层；Task 9 转成线上 OpMsg——Instructions/Assets 元素即 Shared 类型，
/// Strings 在此层仍是裸内容列表，OpMsg 的 StrRef.StrgIndex 由 Task 9 对 boot baseline 填充）。</summary>
public sealed class SwapCodePayload
{
    public string Entry { get; set; } = "";
    public List<SemInstruction> Instructions { get; set; } = new();
    public List<string> Variables { get; set; } = new();
    public List<string> Functions { get; set; } = new();
    public List<string> Strings { get; set; } = new();
    public List<AssetRef> Assets { get; set; } = new();
}
