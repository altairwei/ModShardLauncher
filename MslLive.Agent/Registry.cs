namespace MslLive.Agent;

/// <summary>函数注册表（内置函数 名→索引）。**Task 11 Step 5 修正版**：
/// 不再用 S2 的双点 AOB 引导（计划模板写于 Task 11 之前）——实测记账全局驱动：
/// base = [RegBasePtrVa]、count = [RegCountVa]，记录步进 0x50、name-first 布局
/// {+0x00 内联名(≤0x40，恰占满则无 NUL), +0x40 funcptr, +0x48 argc, +0x4C 0xFFFFFFFF}。
/// 走表后按双锚校验（sprite_exists/object_get_name），索引跨会话稳定（同 exe build）。
/// 已知 2 对同名别名（psn_account_id_for_pad / xboxone_package_check_license，同 funcptr
/// 同 argc 的 platform stub）——后者覆盖前者无害，走表时记日志。</summary>
public static class Registry
{
    static readonly Dictionary<string, int> index = new();
    public static int Count => index.Count;

    public static bool Bootstrap()
    {
        index.Clear();
        ulong baseAddr = Mem.ReadU64(AgentState.RegBasePtrVa);
        uint count = Mem.ReadU32(AgentState.RegCountVa);
        if (baseAddr == 0 || count < 1000 || count > 100000)
        { AgentState.Fail($"registry globals unreadable (base=0x{baseAddr:X} count={count})"); return false; }

        for (int i = 0; i < count; i++)
        {
            ulong rec = baseAddr + (ulong)i * 0x50;
            string name = Mem.ReadCString(rec, 0x40);
            ulong fn = Mem.ReadU64(rec + 0x40);
            uint tail = Mem.ReadU32(rec + 0x4C);
            if (name.Length == 0 || fn < 0x140000000 || fn >= 0x142000000 || tail != 0xFFFFFFFF)
            { AgentState.Fail($"registry record {i} invalid (name='{name}' fn=0x{fn:X} tail=0x{tail:X})"); return false; }
            if (index.ContainsKey(name))
                AgentState.Log($"registry alias: {name} at {index[name]} and {i} (same-fn platform stub, last wins)");
            index[name] = i;
        }

        if (IndexOf(AgentState.RegAnchorName1) != AgentState.RegAnchorIdx1 ||
            IndexOf(AgentState.RegAnchorName2) != AgentState.RegAnchorIdx2)
        {
            AgentState.Fail($"registry anchors mismatch: {AgentState.RegAnchorName1}={IndexOf(AgentState.RegAnchorName1)} (expect {AgentState.RegAnchorIdx1}), " +
                            $"{AgentState.RegAnchorName2}={IndexOf(AgentState.RegAnchorName2)} (expect {AgentState.RegAnchorIdx2})");
            return false;
        }
        AgentState.Log($"registry walked {index.Count} entries, anchors ok");
        return true;
    }

    public static int IndexOf(string name) => index.GetValueOrDefault(name, -1);
    public static void Rescan() => Bootstrap();   // 注册我们的原生函数后重扫拿真实索引
}
