namespace MslLive.Agent;

public sealed record NodeInfo(ulong Node, ulong Record, uint CodeId, uint StartOff,
    uint Locals, uint Argc, ulong BufPtr, uint BufLen);

public static class NodeIndex
{
    static readonly Dictionary<string, NodeInfo> byName = new();

    /// <summary>S2 字段表验证链（Task 11 Step 5 活体复核过 +0x64/+0x68/+0x88/+0xA0/+0xA4 与
    /// exec +0x00/+0x18）：qword==NodeSigFn 命中 → +0x64==0x00FFFFFF → +0x68→record 且
    /// record+0x00==ExecVtable → +0x80 名字可打印（≤128B）→ 收录。</summary>
    public static int Build()
    {
        byName.Clear();
        if (AgentState.NodeSigFn == 0) return 0;   // 0 永不可能是签名 VA（扫描全零 qword 会爆量）
        foreach (var hit in Mem.ScanQword(AgentState.NodeSigFn))
            if (TryValidate(hit, out string name, out var info))
                byName[name] = info;
        return byName.Count;
    }

    /// <summary>单点验证（测试经 Mem.TestMap 注入合成节点直接调它）。</summary>
    internal static bool TryValidate(ulong hit, out string name, out NodeInfo info)
    {
        name = ""; info = null!;
        if (Mem.ReadU32(hit + 0x64) != 0x00FFFFFF) return false;
        ulong record = Mem.ReadU64(hit + 0x68);
        if (record == 0 || Mem.ReadU64(record) != AgentState.ExecVtable) return false;
        name = Mem.ReadCString(Mem.ReadU64(hit + 0x80), 128);
        if (name.Length == 0) return false;
        info = new NodeInfo(hit, record,
            Mem.ReadU32(hit + 0x88), Mem.ReadU32(hit + 0x9C),
            Mem.ReadU32(hit + 0xA0), Mem.ReadU32(hit + 0xA4),
            Mem.ReadU64(record + 0x18), Mem.ReadU32(record + 0x08));
        return true;
    }

    public static bool TryGet(string name, out NodeInfo info) => byName.TryGetValue(name, out info!);
    public static IEnumerable<NodeInfo> All => byName.Values;
    public static int Count => byName.Count;
}
