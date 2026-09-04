using System.Text;
using MslLive.Agent;
using MslLive.Shared;
using Xunit;

namespace MslLive.Test;

/// <summary>ProofVerify 端到端（合成内存）：TestMap 里种节点 + 活 buffer，
/// 走「节点链 → 校准收割 → 独立编码 → 全等直读」全链。
/// 成功例的活 buffer 操作数就是校准真值（i:spr=1215 经收割进表后被 Translator 复现）；
/// 失败例逐个钉 fail-closed 语义：篡改 / 形态错 / 未解析 / 别名子节点。</summary>
public class ProofVerifyTests : IDisposable
{
    const ulong SIG = 0x1406BE508;      // 与 addresses.h kNodeSigFn 同形（值本身只是扫描靶）
    const ulong EXEC = 0x14066AC48;
    const ulong Base = 0x10000;

    const ulong Node = 0x10100, Record = 0x10800, Name = 0x10C00, Buf = 0x11000;

    public ProofVerifyTests()
    {
        Mem.TestMap = new byte[0x2000];
        Mem.TestBase = Base;
        Mem.TestAllocs.Clear();
        AgentState.ResetForTest();
        AgentState.NodeSigFn = SIG;
        AgentState.ExecVtable = EXEC;
    }

    public void Dispose()
    {
        Mem.TestMap = null;
        Mem.TestAllocs.Clear();
        AgentState.NodeSigFn = AgentState.ExecVtable = 0;
        AgentState.ResetForTest();
    }

    static void W64(ulong addr, ulong v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));
    static void W32(ulong addr, uint v) => BitConverter.GetBytes(v).CopyTo(Mem.TestMap!, (int)(addr - Base));

    /// <summary>种一个全合法节点（StartOff=0），并把 live 字节摆进 bufAt。</summary>
    static void PlantNode(string entry, byte[] live, ulong bufAt = Buf, uint startOff = 0)
    {
        W64(Node, SIG);
        W32(Node + 0x64, 0x00FFFFFF);           // 哨兵
        W64(Node + 0x68, Record);               // → exec record
        W64(Record, EXEC);                      // record +0x00 = exec vtable
        W32(Record + 0x08, (uint)live.Length);  // BufLen
        W64(Record + 0x18, bufAt);              // BufPtr
        W64(Node + 0x80, Name);                 // → name
        Encoding.ASCII.GetBytes(entry).CopyTo(Mem.TestMap!, (int)(Name - Base));
        W32(Node + 0x88, 1234);                 // CodeId
        W32(Node + 0x9C, startOff);
        live.CopyTo(Mem.TestMap!, (int)(bufAt - Base));
    }

    /// <summary>成功例载荷：push.v spr（符号变量，校准收割）+ pushbltn.v argument0（内置 raw smallId）
    /// + popz.v。对应活字节见 LiveOk。</summary>
    static OpMsg SuccessOp() => new()
    {
        Seq = 0, Kind = "swap", Entry = "test_proof",
        Instructions =
        {
            new SemInstruction { Kind = BcEncoder.OpPush, T1 = BcEncoder.TVariable, Inst = -1, Var = "spr", RefTop = 0xA0 },
            new SemInstruction { Kind = BcEncoder.OpPushBltn, T1 = BcEncoder.TVariable, Inst = -1, Var = "argument0", RefTop = 0xA0 },
            new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
        },
    };

    /// <summary>push.v spr → 0xA0|(100000+1215)；pushbltn.v argument0 → 0xA0|93；popz.v。</summary>
    static byte[] LiveOk() => new byte[]
    {
        0xFF, 0xFF, 0x05, 0xC0, 0x5F, 0x8B, 0x01, 0xA0,
        0xFF, 0xFF, 0x05, 0xC3, 0x5D, 0x00, 0x00, 0xA0,
        0x00, 0x00, 0x05, 0x9E,
    };

    static ProofAck RunOne(OpMsg op)
    {
        Assert.True(NodeIndex.Build() > 0 || op.Entry == "nope");
        return ProofVerify.Run(new ProofMsg { Ops = { op } });
    }

    [Fact]
    public void Success_Verifies_AndCalibrates()
    {
        PlantNode("test_proof", LiveOk());
        var ack = RunOne(SuccessOp());
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
        Assert.Equal(0, ack.Failed);
        // 校准收割真实发生：活 buffer 读回的 1215 进表（Translator 正是靠它复现字节）
        Assert.Equal(1215, VarCalibrator.Map["i:spr"]);
    }

    [Fact]
    public void TamperedNonOperandByte_Fails()
    {
        var live = LiveOk();
        live[19] = 0x9F;   // popz 的 opcode 字节被改写——收割不受影响，全等直读必须抓
        PlantNode("test_proof", live);
        var ack = RunOne(SuccessOp());
        Assert.False(ack.Ok);
        Assert.Equal(1, ack.Failed);
        Assert.Contains("encoded != live", ack.Error);
    }

    [Fact]
    public void TopByteMismatch_FailsFormCheck()
    {
        var live = LiveOk();
        live[7] = 0x80;   // spr 操作数顶字节 0x80 ≠ 载荷 RefTop 0xA0
        PlantNode("test_proof", live);
        var ack = RunOne(SuccessOp());
        Assert.False(ack.Ok);
        Assert.Contains("top byte", ack.Error);
    }

    [Fact]
    public void UnresolvedOperand_FailsFormCheck()
    {
        var live = LiveOk();
        // low24 = 500 < 100000 且非内置名 → VM 加载未重写的死占位形态
        live[4] = 0xF4; live[5] = 0x01; live[6] = 0x00;
        PlantNode("test_proof", live);
        var ack = RunOne(SuccessOp());
        Assert.False(ack.Ok);
        Assert.Contains("unresolved", ack.Error);
    }

    [Fact]
    public void NodeMissing_Fails()
    {
        NodeIndex.Build();   // 空 TestMap → 0 节点
        var ack = ProofVerify.Run(new ProofMsg { Ops = { new OpMsg { Entry = "nope" } } });
        Assert.False(ack.Ok);
        Assert.Contains("node not found", ack.Error);
    }

    /// <summary>E2E smoke 首证（09-05 00:40 沙箱）：语料里的 wrapper 裸根（scr_e2e_probe 等）
    /// 在索引里无节点（#17：exec 节点按绑定创建，SCPT 指子不指根）——proof 必须与
    /// ApplyEngine.Prepare / CalibOps 同源：直名 miss → gml_Script_+裸名回退把住共享 buffer。</summary>
    [Fact]
    public void RootEntry_Miss_FallsBackToScriptChild()
    {
        PlantNode("gml_Script_scr_x", LiveOk());
        var op = SuccessOp();
        op.Entry = "scr_x";
        var ack = RunOne(op);
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
    }

    /// <summary>子条目直名命中（StartOff≠0）在 proof 不是死路：载荷 = 子自己的指令流，
    /// 对照面 = 共享 buffer 的 [StartOff, +ΣByteSize) 子区段（ProofBuilder 不过滤子条目，
    /// seed 语料必含 gml_Script_*；尾部 wrapper 字节不在对照面内）。载荷长先算后切，
    /// 避开「先编码才知道长」的循环依赖。</summary>
    [Fact]
    public void ChildEntry_VerifiesAgainstSubRange()
    {
        var child = BcEncoder.Encode(PopzChain(3).Instructions, new Translator(PopzChain(3)));   // 12B
        var whole = new byte[24];   // [4B wrapper 头][12B 子体][8B wrapper 尾]
        for (int i = 0; i < 4; i++) whole[i] = 0x11;
        for (int i = 16; i < 24; i++) whole[i] = 0x22;
        child.CopyTo(whole, 4);
        PlantNode("gml_Script_scr_x", whole, startOff: 4);
        var op = PopzChain(3);
        op.Entry = "gml_Script_scr_x";
        var ack = RunOne(op);
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
    }

    /// <summary>子区段对照不是空过：子体区域内的字节篡改必须被抓（wrapper 头/尾不在面内，
    /// 改了不应影响——由 Verify 测试的 whole 反证）。</summary>
    [Fact]
    public void ChildEntry_TamperedSubRange_Fails()
    {
        var child = BcEncoder.Encode(PopzChain(3).Instructions, new Translator(PopzChain(3)));
        var whole = new byte[24];
        child.CopyTo(whole, 4);
        whole[6] ^= 0xFF;   // 子区段内（偏移 4+2）
        PlantNode("gml_Script_scr_x", whole, startOff: 4);
        var op = PopzChain(3);
        op.Entry = "gml_Script_scr_x";
        var ack = RunOne(op);
        Assert.False(ack.Ok);
        Assert.Contains("encoded != live", ack.Error);
    }

    /// <summary>子区段越界（StartOff+载荷长 &gt; buffer 长）= 形态错，fail-closed 明文案。</summary>
    [Fact]
    public void ChildEntry_SubRangeOverrun_Fails()
    {
        PlantNode("test_proof", LiveOk(), startOff: 5);   // 20B buffer，载荷 20B → 5+20 越界
        var ack = RunOne(SuccessOp());
        Assert.False(ack.Ok);
        Assert.Contains("sub-range", ack.Error);
    }

    /// <summary>E2E seed 首证（09-05 00:40 沙箱）：脚本调用操作数 = 100000+<b>脚本表序</b>，非
    /// 100000+node+0x88（CODE 索引）。真机两空间重合（StoneShard CODE chunk 以脚本序打头，
    /// 脚本 i 的 CODE 索引 == SCPT 序号）掩盖了分离；seed 上 scr_e2e_probe=CODE[2] 但 SCPT[0]，
    /// 观察者活体 0x186A0 = 100000+0 且 e2e_probe_result.txt=111 证明该操作数真解析到探针
    /// （排除 raw FUNC 索引假设）。校准值必须胜过 +0x88 兜底（PlantNode 恒写 CodeId=1234 作陷阱）。</summary>
    [Fact]
    public void ScriptCall_CalibratedId_BeatsCodeIdFallback()
    {
        // 活 buffer：call.i gml_Script_scr_x(argc=0)，操作数 = 100000+0（脚本表序 0）
        byte[] live =
        {
            0x00, 0x00, 0x02, 0xD9,   // 指令字：0xD9<<24 | T1=TInt32<<16 | argc=0
            0xA0, 0x86, 0x01, 0x00,   // 操作数 0x000186A0 = 100000+0
            0x00, 0x00, 0x05, 0x9E,   // popz.v 收尾
        };
        PlantNode("test_proof", live);
        var op = new OpMsg
        {
            Seq = 0, Kind = "swap", Entry = "test_proof",
            Instructions =
            {
                new SemInstruction { Kind = BcEncoder.OpCall, T1 = BcEncoder.TInt32, Low16 = 0, Fn = "gml_Script_scr_x" },
                new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable },
            },
        };
        var ack = RunOne(op);
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
        // 收割真实发生：脚本表序 0 进表（+0x88 兜底会编码 100000+1234 ≠ 活体）
        Assert.Equal(0, CallCalibrator.Map["gml_Script_scr_x"]);
    }

    static OpMsg PopzChain(int n)
    {
        var op = new OpMsg { Seq = 0, Kind = "swap", Entry = "test_proof" };
        for (int i = 0; i < n; i++)
            op.Instructions.Add(new SemInstruction { Kind = BcEncoder.OpPopz, T1 = BcEncoder.TVariable });
        return op;
    }

    [Fact]
    public void LongBuffer_Verifies()
    {
        var live = BcEncoder.Encode(PopzChain(12).Instructions, new Translator(PopzChain(12)));   // 48B
        Assert.Equal(48, live.Length);
        PlantNode("test_proof", live);
        var ack = RunOne(PopzChain(12));
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
    }

    [Fact]
    public void LongBuffer_DuplicateCopyElsewhere_StillVerifies()
    {
        // fix-loop #13（真机 07:10）：35/35 op 全死在旧的头窗全局唯一性检查——
        // agent 自己的 live/encoded 临时副本就躺在被扫的进程内 GC 堆上（Mem.ScanAob
        // 枚举本进程全地址空间），命中数结构性 ≥3（BufPtr+live+encoded），检查逻辑上
        // 永不可能通过；walk 结束 GC 清场后外部复扫全进程只剩 BufPtr 一 hit，证实多
        // 命中是自扫伪影而非游戏内存真有副本。本测试把「同字节副本在别处存在」钉成
        // 不构成失败：全等直读只对节点链指向的 buffer 本身负责，运行时无任何路径
        // 靠 AOB 定位（apply/trampoline 全部 record+0x18 直达）。
        var live = BcEncoder.Encode(PopzChain(12).Instructions, new Translator(PopzChain(12)));
        PlantNode("test_proof", live);
        live.CopyTo(Mem.TestMap!, (int)(0x11800 - Base));   // 同字节第二份 → 不再影响自证
        var ack = RunOne(PopzChain(12));
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal(1, ack.Verified);
    }
}
