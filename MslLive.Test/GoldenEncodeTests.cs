using System.Text.Json;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>黄金测试（Task 14 离线真门）：S2 的 172 差异字节翻译规则变成 CI 里的逐字节断言。
/// 两侧夹击：文件形态编码 == sigdump 文件字节（payload 忠实性 + 非翻译字段无损往返），
/// 运行时形态编码 == 活体 buffer（编码器 + 翻译规则 + ground-truth 解析全链）。
/// golden 文件是 runner 的真实输出，<b>禁止改 golden</b>——红了只许改编码器/翻译器。</summary>
public class GoldenEncodeTests
{
    static OpMsg LoadPayload()
    {
        var op = JsonSerializer.Deserialize<OpMsg>(
            File.ReadAllText("Assets/payload_unitRenderDrawSprite.json"))!;
        Assert.NotNull(op);
        return op;
    }

    [Fact]
    public void Payload_Shape_AndStringBindings_MatchS2GroundTruth()
    {
        var op = LoadPayload();
        Assert.Equal(110, op.Instructions.Count);      // S2 ①：父 buffer = 110 条
        Assert.Equal(1, op.LocalsCount);               // wrapper 节点 locals=1
        // S2 ③：运行时 stringId == STRG 索引（生成器从 data.win 绑的索引必须等于 S2 实测）
        Assert.Equal(2480, op.Strings.Single(s => s.Content == "Unit ").StrgIndex);
        Assert.Equal(2481, op.Strings.Single(s => s.Content == " doesn't have sprite!").StrgIndex);
        Assert.Equal(2482, op.Strings.Single(s => s.Content == "UNIT SPRITE ERROR").StrgIndex);
        Assert.Empty(op.Assets);
    }

    [Fact]
    public void Encode_S2Entry_MatchesLiveBufferByteForByte()
    {
        var op = LoadPayload();
        var golden = File.ReadAllBytes("Assets/buf_unitRenderDrawSprite.bin");
        Assert.Equal(712, golden.Length);
        byte[] encoded = BcEncoder.Encode(op.Instructions, new S2GroundTruthResolver());
        Assert.Equal(golden, encoded);   // 712 字节逐字节相等
    }

    [Fact]
    public void Encode_S2Entry_FileForm_MatchesSigdumpByteForByte()
    {
        var op = LoadPayload();
        var fileForm = File.ReadAllBytes("Assets/buf_unitRenderDrawSprite_file.bin");
        Assert.Equal(712, fileForm.Length);
        byte[] encoded = BcEncoder.Encode(op.Instructions, new FileFormResolver());
        Assert.Equal(fileForm, encoded);   // 非翻译字段（opcode 字/分支/字面量/RefTop）无损往返
    }

    [Fact]
    public void Encode_PushEBreakEInt16Literals_WriteValueIntoWord()
    {
        // fix-loop #14 真机 golden（bufdump @StoneShard#28080 只读直取活体 buffer）：
        //   push.e 1   → 01 00 0F C0（布尔物化舞步：cmp; b; push.e 1; bf）
        //   break.e -5 → FB FF 0F FF（数组边界标记：push.i 983040; break.e -5）
        // S2 黄金 entry 的 110 条指令里没有这两类形态（已核）——覆盖洞正是 #14 溜到真机才暴露的原因。
        // push.e 此前落进 TypeInst 臂（Inst=Undefined→写 0）；break.e 走 Cat.Break verbatim Low16，
        // 值由提取侧 #14 修复携带。
        var sems = new List<SemInstruction>
        {
            new() { Kind = 0xC0, T1 = 0x0F, T2 = 0x00, Low16 = 0x0001, Int = 1 },   // push.e 1
            new() { Kind = 0xFF, T1 = 0x0F, T2 = 0x00, Low16 = 0xFFFB, Int = -5 },  // break.e -5
        };
        byte[] encoded = BcEncoder.Encode(sems, new FileFormResolver());
        Assert.Equal(new byte[] { 0x01, 0x00, 0x0F, 0xC0, 0xFB, 0xFF, 0x0F, 0xFF }, encoded);
    }

    /// <summary>文件形态解析器：引用占位 0xDEAD（变量 top 字节由编码器从 RefTop 回填）、
    /// 字符串占位 0——即 runner 装载前的静态形态（findings-t11：0x?000DEAD）。</summary>
    sealed class FileFormResolver : IOperandResolver
    {
        public int ResolveString(StrRef s) => 0;
        public int ResolveCall(string fn) => 0xDEAD;
        public uint ResolveVar(string name, short instType) => 0xDEAD;
        public int ResolveAsset(AssetRef a) => (int)a.Index;
    }

    /// <summary>S2 实测 ground truth（findings-s2 ② 记录值 + 黄金操作数解码值；
    /// 后者与 Task 7/11 独立活体 truths 逐点交叉一致：stScaleY..stX=312..315、_color=484、
    /// _borderLeft..Bottom=646..649、isGround=716、spr=1215、waterDrawState=1216、
    /// scr_unitRenderDrawSprite=1217、draw_sprite_ext=484 注册表索引、sprite_exists=645、
    /// object_get_name=761）。返回 low24 语义值（IOperandResolver 契约）。</summary>
    sealed class S2GroundTruthResolver : IOperandResolver
    {
        static readonly Dictionary<string, int> Strings = new()
        {
            ["Unit "] = 2480,
            [" doesn't have sprite!"] = 2481,
            ["UNIT SPRITE ERROR"] = 2482,
        };
        static readonly Dictionary<string, int> Calls = new()
        {
            // 内置函数 = 注册表原始索引（Task 11 新发现 3）
            ["sprite_exists"] = 645, ["object_get_name"] = 761,
            ["sprite_get_width"] = 648, ["sprite_get_height"] = 649, ["sprite_get_yoffset"] = 651,
            ["draw_sprite_ext"] = 484, ["method"] = 160,
            // 脚本 = 100000 + codeId（S2 ② 四重验证 + push.v 函数引用）
            ["gml_Script_show_error_message"] = 106374,
            ["gml_Script_gpu_get_blendmode_normal"] = 101350,
            ["gml_Script_color_premultalpha"] = 101414,
            ["gml_Script_scr_drawSpritePart"] = 106066,
            ["gml_Script_scr_unitRenderDrawSprite"] = 100527,
        };
        static readonly Dictionary<string, uint> Vars = new()
        {
            // 内置变量 = raw smallId（无偏置，S2 黄金实测；本表字段回答 Task 11 未决项）
            ["argument0"] = 93, ["sprite_index"] = 26, ["image_index"] = 27,
            ["object_index"] = 14, ["image_alpha"] = 37, ["image_blend"] = 38, ["image_angle"] = 36,
            // 符号表变量（局部/实例/全局/stacktop 同空间）= 100000 + loadOrderId
            ["_borderLeft"] = 100646, ["_borderTop"] = 100647,
            ["_borderRight"] = 100648, ["_borderBottom"] = 100649,
            ["_color"] = 100484, ["spr"] = 101215, ["isGround"] = 100716,
            ["waterDrawState"] = 101216,
            ["stScaleY"] = 100312, ["stScaleX"] = 100313, ["stY"] = 100314, ["stX"] = 100315,
            ["scr_unitRenderDrawSprite"] = 101217,
        };

        public int ResolveString(StrRef s) =>
            Strings.TryGetValue(s.Content, out int id) ? id : throw new Xunit.Sdk.XunitException($"unmapped string '{s.Content}'");
        public int ResolveCall(string fn) =>
            Calls.TryGetValue(fn, out int id) ? id : throw new Xunit.Sdk.XunitException($"unmapped call '{fn}'");
        public uint ResolveVar(string name, short instType) =>
            Vars.TryGetValue(name, out uint id) ? id : throw new Xunit.Sdk.XunitException($"unmapped var '{name}'");
        public int ResolveAsset(AssetRef a) => throw new Xunit.Sdk.XunitException("golden entry has no asset refs");
    }
}
