using System;
using System.Security.Cryptography;
using System.Text;

namespace ModShardLauncher.HotReload;

/// <summary>[v2 Task 3] 文本 → SHA256 hex（账本/终稿比较键；不用 string.GetHashCode——进程随机化且碰撞不可控）。</summary>
public static class TextHash
{
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
