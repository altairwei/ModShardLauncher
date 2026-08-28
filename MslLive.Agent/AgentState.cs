using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("MslLive.Test")]

namespace MslLive.Agent;

/// <summary>agent 进程内全局状态。常量字段为 public static 可变（测试可直接注入合成值；
/// 产品侧只有 Init 写一次）。Fail 累积进 Status（"ok"=全过），随 hello 上报。</summary>
public static class AgentState
{
    public static string GameDir = "";
    public static ulong FunctionAdd, NodeSigFn, ExecVtable;
    public static ulong RegBasePtrVa, RegCountVa;
    public static int RegAnchorIdx1, RegAnchorIdx2;
    // 锚名常量 = addresses.h kRegAnchorName1/2 的 C# 侧镜像（名字非地址；改地址表时同步）
    public const string RegAnchorName1 = "sprite_exists";
    public const string RegAnchorName2 = "object_get_name";

    public static string BootHash = "";
    public static string AgentVersion = "";
    public static bool StubPresent;
    public static Dictionary<string, int>? VarMap;   // vars 信封（MSL→agent 单向）
    public static readonly BlanksState Blanks = new();

    static readonly object statusLock = new();
    static string status = "ok";
    public static string Status { get { lock (statusLock) return status; } }

    static StreamWriter? log;

    public static unsafe void Init(BootArgs* args)
    {
        FunctionAdd = args->FunctionAdd; NodeSigFn = args->NodeSigFn; ExecVtable = args->ExecVtable;
        RegBasePtrVa = args->RegBasePtrVa; RegCountVa = args->RegCountVa;
        RegAnchorIdx1 = args->RegAnchorIdx1; RegAnchorIdx2 = args->RegAnchorIdx2;
        InitCore(Marshal.PtrToStringUni(args->GameDir) ?? "");
    }

    /// <summary>测试最小切面：只挂目录/日志/版本/哈希，常量留 0（自检走 fail 路径且不崩，
    /// 由 Mem 的守卫读保证）。</summary>
    public static void InitForTest(string gameDir) => InitCore(gameDir);

    static void InitCore(string gameDir)
    {
        GameDir = gameDir;
        Directory.CreateDirectory(Path.Combine(GameDir, "msllive"));
        log?.Dispose();   // 重复 Init（测试）时先放掉旧 writer，否则二次打开同一 agent.log 撞文件锁
        log = new StreamWriter(Path.Combine(GameDir, "msllive", "agent.log"), append: true) { AutoFlush = true };
        AgentVersion = "v" + (FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion ?? "0.0.0.0");
        string dataWin = Path.Combine(GameDir, "data.win");
        if (File.Exists(dataWin))
        {
            // 尽早原则：Init 里立刻算（流式，避免几百 MB 的 ReadAllBytes）
            using var fs = File.OpenRead(dataWin);
            using var sha = SHA256.Create();
            BootHash = Convert.ToHexString(sha.ComputeHash(fs));   // 大写 hex（ComputeHash(Stream) 全版本可用）
        }
        Log($"agent init: dir={GameDir} ver={AgentVersion} hash={(BootHash.Length > 0 ? BootHash[..8] : "<no data.win>")}");
    }

    public static void Fail(string why)
    {
        lock (statusLock) status = status == "ok" ? why : status + "; " + why;
        Log("FAIL: " + why);
    }

    public static void Log(string msg)
    {
        try { log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { /* 日志永不致命 */ }
    }

    /// <summary>测试复位（自检/校准/trampoline/apply 队列积累的状态）。</summary>
    public static void ResetForTest()
    {
        lock (statusLock) status = "ok";
        StubPresent = false;
        VarMap = null;
        Blanks.Reset();
        VarCalibrator.ResetForTest();
        Trampoline.ResetForTest();
        ApplyEngine.ResetForTest();
    }

    public sealed class BlanksState
    {
        public int SpriteFirst = -1, SpriteCount, PathFirst = -1, PathCount;
        public bool Ready;
        public void Set(int spriteFirst, int pathFirst)
        { SpriteFirst = spriteFirst; PathFirst = pathFirst; Ready = true; }
        public void Reset() { SpriteFirst = PathFirst = -1; SpriteCount = PathCount = 0; Ready = false; }
    }
}
