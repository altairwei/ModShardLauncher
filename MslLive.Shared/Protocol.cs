namespace MslLive.Shared;

/// <summary>SwapCode 线格式：一条指令的语义形态（移植自 Spike S4Tools，字段不变）。
/// agent 端按 Kind/T1/T2 重编码为运行时格式，操作数位置由指令序列顺序隐含。</summary>
public sealed class SemInstruction
{
    public byte Kind { get; set; }
    public byte T1 { get; set; }
    public byte T2 { get; set; }
    public short Inst { get; set; }
    public ushort Low16 { get; set; }
    /// <summary>文件形态操作数的顶字节（= UTMT ReferenceType：Normal 0xA0 / Stacktop 0x80 …）。
    /// 静态占位 0x?000DEAD 的顶字节在文件侧就已就位（findings-t11 新发现 4），VM 加载只重写 low24——
    /// 编码器对变量引用操作数原样回填本字段，只翻译 low24。非变量引用指令恒为 0。</summary>
    public byte RefTop { get; set; }
    public string? Var { get; set; }
    public string? Fn { get; set; }
    public string? Str { get; set; }
    public long? Int { get; set; }
    public double? Real { get; set; }
    public int? Jump { get; set; }
    public byte? Cmp { get; set; }
    public List<string>? AssetKinds { get; set; }
}

public sealed class AssetRef
{
    public string Kind { get; set; } = "";
    public long Index { get; set; }          // product 侧索引（载荷里出现的字面量值）
    public string? Name { get; set; }
    public long RuntimeIndex { get; set; } = -1; // MSL overlay 解析；baseline 区间资产 = Index 原值
}

public sealed class StrRef
{
    public string Content { get; set; } = "";
    public int StrgIndex { get; set; } = -1;   // boot STRG 索引 == 运行时 stringId；-1 = 非 boot 字符串 → agent 拒
}

public sealed class OpMsg
{
    public int Seq { get; set; }
    public string Kind { get; set; } = "swap";   // "swap" | "loader" | "trigger" | "restore"
    public string Entry { get; set; } = "";
    public int LocalsCount { get; set; }
    public int ArgCount { get; set; }
    public List<SemInstruction> Instructions { get; set; } = new();
    public List<string> Variables { get; set; } = new();
    public List<string> Functions { get; set; } = new();
    public List<StrRef> Strings { get; set; } = new();
    public List<AssetRef> Assets { get; set; } = new();
    public bool ExecuteOnce { get; set; }        // trigger/restore 专用语义见 Task 15
}

public sealed class BatchMsg
{
    public int Protocol { get; set; } = 1;
    public int BatchSeq { get; set; }
    public List<OpMsg> Ops { get; set; } = new();
    /// <summary>#21 校准语料：baseline 未改动 entry 的载荷（同 ProofMsg.Ops 形态），agent 在翻译
    /// 本批任何 op 之前先对其逐个 VarCalibrator.Harvest——变量 id 的唯一真源是 runner 已回填的
    /// 活 buffer。只收 ParentEntry==null 的根条目（子条目流与共享 buffer 基址错位 4B 不可作语料）。</summary>
    public List<OpMsg> CalibOps { get; set; } = new();
}

public sealed class OpReceipt
{
    public int Seq { get; set; }
    public string Entry { get; set; } = "";
    public bool Ok { get; set; }
    public string Stage { get; set; } = "";      // "resolve" | "validate"（commit 设计为不可失败）
    public string Reason { get; set; } = "";
    public bool RequiresRestart { get; set; }
    /// <summary>#30 借位 id 明细（"l:名→id"）：本 op 经借位分配的局部变量——范围内未用 id，
    /// 纯外观风险（报错文案打印成别人的名字——findings 2026-09-04 §五）。</summary>
    public List<string> BorrowedIds { get; set; } = new();
    /// <summary>#33 激活门 count patch：boot 帧局部数 0 + 载荷用局部时，commit 窗口把帧计数
    /// patch 到的值（0 = 本 op 无 patch）。全别名 node+0xA0/record+0x0C 同批写——
    /// invoker 每次调用读 node+0xA0 新建容器，count 唯一读者是激活门（==0 跳过）。</summary>
    public uint LocalsPatched { get; set; }
}

public sealed class BatchReceipt
{
    public int BatchSeq { get; set; }
    public bool AllOk { get; set; }
    public List<OpReceipt> Ops { get; set; } = new();
    public long ApplyMs { get; set; }
}

public sealed class HelloMsg
{
    public int Protocol { get; set; } = 1;
    public string AgentVersion { get; set; } = "";
    public int Pid { get; set; }
    public string BootHash { get; set; } = "";      // bootstrap 线程启动时尽快对磁盘 data.win 的 SHA256（大写 hex）
    public bool StubPresent { get; set; }           // NodeIndex 含 gml_Object_o_msl_live_Step_0
    public string AgentStatus { get; set; } = "ok"; // != "ok" = agent 自检失败自述（MSL 拒并展示）
}

public sealed class HelloAck
{
    public bool Accept { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BlanksMsg
{
    public int SpriteFirst { get; set; } = -1;
    public int SpriteCount { get; set; }
    public int PathFirst { get; set; } = -1;
    public int PathCount { get; set; }
}

public sealed class VarsMsg
{
    public int Protocol { get; set; } = 1;
    public Dictionary<string, int> Ids { get; set; } = new();   // "i:"+名 / "g:"+名 / "l:"+名（局部——fix-loop #16 [V]：845 实证 Local 与实例并存，分域防串 id）→ loadOrderId
}

public sealed class ProofMsg
{
    public List<OpMsg> Ops { get; set; } = new();  // 未改动 baseline entries 的载荷（只编码，不应用）
}

public sealed class ProofAck
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int Verified { get; set; }
    public int Failed { get; set; }
}
