using System.Runtime.InteropServices;

namespace MslLive.Agent;

// 与 MslLive.Bootstrap dllmain.cpp 的 BootArgs 逐字段对应（LayoutKind.Sequential）。
// Task 13 偏差记录：尾部新增 RegBasePtrVa/RegCountVa（Task 11 Step 5 发现的注册表记账全局），
// 计划模板的 BootArgs 无此二字段——注册表走表已从 S2 双点 AOB 改为全局驱动（见 Registry.cs）。
[StructLayout(LayoutKind.Sequential)]
public struct BootArgs
{
    public IntPtr GameDir;      // wchar_t*（UTF-16）
    public ulong FunctionAdd;
    public ulong NodeSigFn;
    public ulong ExecVtable;
    public ulong RegBasePtrVa;
    public ulong RegCountVa;
    public int RegAnchorIdx1;
    public int RegAnchorIdx2;
}

public static unsafe class Boot
{
    [UnmanagedCallersOnly]
    public static int Main(BootArgs* args)
    {
        try
        {
            AgentState.Init(args);
            PipeServer.Start();
            return 0;
        }
        catch (Exception ex)
        {
            AgentState.Log("boot failed: " + ex);
            return 1;
        }
    }

    // x64 只有一个调用约定，Stdcall/Cdecl 之辨是 x86 遗产；保留显式标注与 bootstrap 侧注释呼应。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    public static void OnInitGML()
    {
        try
        {
            NativeRegistration.RegisterAll();
            // fix-loop #9：boot 期后台建索引——游戏刚建完 exec 节点（扫描得见全量）、
            // data.win 页多驻留（构建在秒级）；此前在首连 SelfCheck 里同步跑，真机实测
            // 26min43s（游戏工作集被扫描逐出 → 38K 命中散读全是页错误），hello 迟到超时。
            NodeIndex.BeginBuild();
        }
        catch (Exception ex) { AgentState.Log("RegisterAll failed: " + ex); }
    }
}
