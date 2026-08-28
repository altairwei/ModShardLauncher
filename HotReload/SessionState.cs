using System;
using System.Collections.Generic;
using System.Linq;
using MslLive.Shared;

namespace ModShardLauncher.HotReload;

public sealed class PoolExhaustedException : Exception
{
    public string PoolKind { get; }
    public PoolExhaustedException(string poolKind, string detail)
        : base($"{poolKind} pool exhausted: {detail}") => PoolKind = poolKind;
}

/// <summary>一次热会话的分配状态：名字 → 运行时载体的映射，跨迭代稳定（同名同槽）。
/// 冷启动收编（spec §6.3）：会话结束即弃，无任何持久化。</summary>
public sealed class SessionState
{
    readonly LiveQuotas quotas;
    readonly Queue<int> freeScriptSlots;
    readonly List<(int shellIndex, string parent)> shells;
    readonly List<int> freeShells = new();   // Queue 没有 Remove——壳要按父类桶挑选，用 List
    readonly Queue<int> freeRooms;
    BlanksMsg blanks = new();
    int nextSprite, nextPath;

    public Dictionary<string, string> ScriptSlots { get; } = new();   // scr_new -> msl_slot_17
    public Dictionary<string, int> SpriteBlanks { get; } = new();     // s_new  -> runtime sprite index
    public Dictionary<string, int> ObjectShells { get; } = new();     // o_new  -> shell 运行时对象索引
    public Dictionary<string, int> RoomSlots { get; } = new();        // r_new  -> 空房间运行时索引

    public SessionState(LiveQuotas quotas, IReadOnlyList<(int shellIndex, string parent)> shellBuckets)
    {
        this.quotas = quotas;
        shells = shellBuckets.ToList();
        freeScriptSlots = new Queue<int>(Enumerable.Range(0, quotas.ScriptSlots));
        freeRooms = new Queue<int>(Enumerable.Range(0, quotas.EmptyRooms));
        foreach (var (idx, _) in shells) freeShells.Add(idx);
    }

    public void SetBlanks(BlanksMsg b)
    {
        blanks = b;
        if (nextSprite == 0) nextSprite = b.SpriteFirst;
        if (nextPath == 0) nextPath = b.PathFirst;
    }

    public string AllocateScript(string name)
    {
        if (ScriptSlots.TryGetValue(name, out var s)) return s;
        if (freeScriptSlots.Count == 0) throw new PoolExhaustedException("script-slot", name);
        s = $"msl_slot_{freeScriptSlots.Dequeue()}";
        ScriptSlots[name] = s;
        return s;
    }

    public int AllocateSprite(string name)
    {
        if (SpriteBlanks.TryGetValue(name, out var i)) return i;
        if (blanks.SpriteFirst < 0) throw new PoolExhaustedException("blank-sprite", "blanks not reported yet");
        if (nextSprite >= blanks.SpriteFirst + blanks.SpriteCount)
            throw new PoolExhaustedException("blank-sprite", name);
        SpriteBlanks[name] = nextSprite++;
        return SpriteBlanks[name];
    }

    public int AllocateShell(string name, string? parentName)
    {
        if (ObjectShells.TryGetValue(name, out var i)) return i;
        // 父类桶匹配（spec §6.2）：无父对象配 "" 桶；父类不在任何桶 → 重启类（fail-closed）
        string parent = parentName ?? "";
        int shell = shells.Where(s => s.parent == parent && freeShells.Contains(s.shellIndex))
            .Select(s => s.shellIndex).FirstOrDefault(-1);
        if (shell < 0) throw new PoolExhaustedException("shell-object", $"{name} (parent '{parent}' has no bucket)");
        freeShells.Remove(shell);
        ObjectShells[name] = shell;
        return shell;
    }

    public int AllocateRoom(string name)
    {
        if (RoomSlots.TryGetValue(name, out var i)) return i;
        if (freeRooms.Count == 0) throw new PoolExhaustedException("empty-room", name);
        int slot = freeRooms.Dequeue();
        // 空房间的运行时索引 = boot data 房间数（池在末尾追加）+ 槽序号——由 Task 8 注入顺序保证
        i = quotas.RoomBaseIndex + slot;
        RoomSlots[name] = i;
        return i;
    }
}
