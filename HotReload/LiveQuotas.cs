namespace ModShardLauncher.HotReload;

public sealed class LiveQuotas
{
    public int ScriptSlots { get; set; } = 64;
    public int ShellObjects { get; set; } = 8;
    public int EmptyRooms { get; set; } = 4;
    public int BlankSprites { get; set; } = 64;
    public int BlankPaths { get; set; } = 16;
    /// <summary>壳对象父类桶（长度须 == ShellObjects；"" = 无父通用桶）。v1 静态列表（取舍清单 2）。</summary>
    public string[] ShellParents { get; set; } =
        { "o_button", "o_button", "o_menuParent", "o_menuParent", "", "", "", "" };
    /// <summary>空房间在 boot data 房间表中的起始索引（注入时回填，Task 8 写、SessionState 读）。</summary>
    public int RoomBaseIndex { get; set; }
}
