using ModShardLauncher.HotReload;
using Xunit;

namespace ModShardLauncherTest;

/// <summary>[v2 Task 3] 文本哈希：SHA256 hex，确定性 + 64 字符。</summary>
public class TextHashTests
{
    [Fact]
    public void Hash_SameTextSameValue_DifferentTextDiffers()
    {
        string a = TextHash.Hash("return 1;");
        string b = TextHash.Hash("return 1;");
        string c = TextHash.Hash("return 2;");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }
}
