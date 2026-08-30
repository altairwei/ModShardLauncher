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
            // fix-loop #9：索引构建移 boot 期后台（此前在首连 SelfCheck 里同步跑，真机实测
            // 26min43s——游戏工作集被扫描逐出 → 38K 命中散读全是页错误，hello 迟到超时）。
            // fix-loop #10 更正：该时刻 exec 节点尚未创建（真机两次实测 natives registered
            // 后 ~80ms 扫描 0 命中）——节点是 data.win 装载绑定（注册器之后）才批量出现，
            // BeginBuild 内置有界重试等它。
            NodeIndex.BeginBuild();
        }
        catch (Exception ex) { AgentState.Log("RegisterAll failed: " + ex); }
    }
}
