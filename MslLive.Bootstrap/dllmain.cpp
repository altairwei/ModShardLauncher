// MslLive.Bootstrap — version.dll 代理 + hostfxr 拉起 MslLive.Agent。
// 安全总线：任一步失败 → 记日志 + 返回（游戏继续原生运行；stub 无 agent 无害休眠）。
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <intrin.h>   // _ReturnAddress()
#include <string>
#include "addresses.h"
#include "vendor/minhook/include/MinHook.h"
#include "vendor/hostfxr.h"
#include "vendor/coreclr_delegates.h"

namespace {

FILE* g_log = nullptr;

void Log(const char* fmt, ...)
{
    if (!g_log) return;
    va_list ap; va_start(ap, fmt);
    vfprintf(g_log, fmt, ap); va_end(ap);
    fflush(g_log);
}

std::wstring DirOf(const std::wstring& path)
{
    auto pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? L"." : path.substr(0, pos);
}

// ---- 崩溃现场记录（VEH，Task 16 真机诊断引入）：AV 第一现场先于 WER 落盘。
// 只记不吞（CONTINUE_SEARCH，游戏自身的异常处理照常）；条数上限防「日志里再崩」的
// 递归——若日志截断，最后一条即真凶。
// 复盘 20:47 崩溃（WER: version.dll+0x11A47 = CRT printf 内部 lambda）后的第二课：
// 异常现场禁止 CRT。本 VEH 以第一优先级挂全进程，CLR 启动期的首违例会落在从未初始化
// 过本 DLL CRT 线程数据的线程上——在异常分发中途跑 vfprintf/locale 既是嫌疑源也会
// 搅浑现场。故 VEH 只走预分配路径：CreateFile 句柄直写 + 手写十六进制，零 CRT/零堆。
HANDLE g_crashFile = INVALID_HANDLE_VALUE;
DWORD g_bootstrapTid = 0;

void RawWrite(const char* s, size_t n)
{
    if (g_crashFile == INVALID_HANDLE_VALUE) return;
    DWORD w = 0;
    WriteFile(g_crashFile, s, (DWORD)n, &w, nullptr);
}

struct RawBuf { char b[192]; size_t p; };
void Put(RawBuf& r, const char* s) { while (*s && r.p < sizeof(r.b)) r.b[r.p++] = *s++; }
void PutHex(RawBuf& r, uint64_t v)      // 定宽 16 位十六进制：免歧义、免格式化库
{
    static const char kHex[] = "0123456789ABCDEF";
    for (int i = 15; i >= 0 && r.p < sizeof(r.b); i--)
        r.b[r.p++] = kHex[(v >> (i * 4)) & 0xF];
}
void Emit(RawBuf& r) { RawWrite(r.b, r.p); RawWrite("\r\n", 2); }

static bool SafeReadU64(uint64_t addr, uint64_t* out)
{
    __try { *out = *reinterpret_cast<uint64_t*>(addr); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static HMODULE ModOf(uint64_t a)
{
    HMODULE m = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(a), &m);
    return m;
}

static LONG CALLBACK CrashVeh(PEXCEPTION_POINTERS ep)
{
    if (ep->ExceptionRecord->ExceptionCode != STATUS_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;
    static volatile LONG n = 0;
    if (InterlockedIncrement(&n) > 64) return EXCEPTION_CONTINUE_SEARCH;
    DWORD tid = GetCurrentThreadId();

    RawBuf r{ {}, 0 };
    Put(r, "[veh] AV#"); PutHex(r, (uint64_t)n);
    Put(r, " tid "); PutHex(r, tid);
    Put(r, " boot "); PutHex(r, tid == g_bootstrapTid ? 1 : 0);
    Put(r, " kind "); PutHex(r, ep->ExceptionRecord->ExceptionInformation[0]);
    Put(r, " target "); PutHex(r, ep->ExceptionRecord->ExceptionInformation[1]);
    Emit(r);

    // 现场寄存器：kind=RIP 不匹配指令时 = 上下文系伪造（合成异常），寄存器值自会作证
    r.p = 0;
    Put(r, "[veh] rip "); PutHex(r, ep->ContextRecord->Rip);
    HMODULE m = ModOf(ep->ContextRecord->Rip);
    if (m) { Put(r, " mod+"); PutHex(r, ep->ContextRecord->Rip - (uint64_t)m); }
    Put(r, " rsp "); PutHex(r, ep->ContextRecord->Rsp);
    Put(r, " rbp "); PutHex(r, ep->ContextRecord->Rbp);
    Emit(r);
    r.p = 0;
    Put(r, "[veh] rcx "); PutHex(r, ep->ContextRecord->Rcx);
    Put(r, " rdx "); PutHex(r, ep->ContextRecord->Rdx);
    Put(r, " rax "); PutHex(r, ep->ContextRecord->Rax);
    Put(r, " rbx "); PutHex(r, ep->ContextRecord->Rbx);
    Put(r, " rsi "); PutHex(r, ep->ContextRecord->Rsi);
    Put(r, " rdi "); PutHex(r, ep->ContextRecord->Rdi);
    Emit(r);
    r.p = 0;
    Put(r, "[veh] r8 ");  PutHex(r, ep->ContextRecord->R8);
    Put(r, " r9 ");  PutHex(r, ep->ContextRecord->R9);
    Put(r, " r10 "); PutHex(r, ep->ContextRecord->R10);
    Put(r, " r11 "); PutHex(r, ep->ContextRecord->R11);
    Put(r, " r12 "); PutHex(r, ep->ContextRecord->R12);
    Put(r, " r13 "); PutHex(r, ep->ContextRecord->R13);
    Put(r, " r14 "); PutHex(r, ep->ContextRecord->R14);
    Put(r, " r15 "); PutHex(r, ep->ContextRecord->R15);
    Emit(r);

    // 栈扫描：只记落在模块内的值（返回地址）；基址→模块名离线对 WER 模块表
    // 21:28 崩溃复盘：返回地址在 rsp+0x4B8（lambda 0x4B0 帧序言），当时只扫 0x180 全漏。
    // 现扫 4KB；另记 exe 邻域值——MinHook trampoline 是 target±2GB 的 VirtualAlloc 页，
    // 不属于任何模块，只按模块过滤会漏。
    uint64_t rsp = ep->ContextRecord->Rsp;
    int shown = 0;
    for (int i = 0; i < 512 && shown < 64; i++)
    {
        uint64_t v = 0;
        if (!SafeReadU64(rsp + (uint64_t)i * 8, &v)) break;
        HMODULE m2 = ModOf(v);
        if (!m2 && !(v >= 0x130000000ULL && v < 0x150000000ULL)) continue;
        r.p = 0;
        Put(r, "[veh] stk+"); PutHex(r, (uint64_t)(i * 8));
        Put(r, " "); PutHex(r, v);
        if (m2) { Put(r, " m+"); PutHex(r, v - (uint64_t)m2); }
        else    { Put(r, " nearexe"); }
        Emit(r);
        shown++;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// ---- LastRegistrar hook（Task 16 fix-loop #3）：在注册窗口内（游戏线程、原函数返回后）调 managed 注册 ----
// 原 hook 点 kInitGMLFunctions(0x1402CDDAF) 实为编排器 F 的函数中部 merge 标签：
// 入口 rsp≡0 mod 16 → CRT printf 内 movdqa #GP（crash #3，gptest 实证 Win10 19045
// 报 AV(-1)）；且 detour 任何栈消耗都会平移 F 的 rsp 相对访问并错位 epilogue
// （add rsp,0C30h; pop r14; jmp [rax+10h]）——结构性不可用，弃用。
// 改挂 F 内最后一个注册器 0x1403148A0（全 .text 唯一 E8 调用者 = call@0x1402CDFB4）：
// 正规 ABI 函数，E8 调用保证入口 rsp≡8 mod 16，无需对齐垫片。
// 注册纯度：RA 先读、orig 立即调（中间只隔一次局部保存，均在 detour 自身帧内，
// 不触调用者状态）；orig 返回 = 注册表已完整、仍在 F 内游戏线程上，随后以
// RA==kAfterLastRegistrarCall + 一次性标志双门控托管注册。
using LastRegistrarFn = void(*)();
LastRegistrarFn g_origLastRegistrar = nullptr;
using OnInitGmlFn = void(*)();
OnInitGmlFn g_onInitGML = nullptr;
volatile bool g_managedReady = false;
volatile bool g_registered = false;   // 防御性：唯一 E8 调用者 + F 启动期一次性；若意外重入，首个完成注册

void DetourLastRegistrar()
{
    void* ra = _ReturnAddress();          // mov rax,[rsp]（编译器补偿帧偏移）；寄存器随后可随意用
    g_origLastRegistrar();                // 立即放行原注册器——在此之前不做任何可能扰 ABI 的事
    if (ra != reinterpret_cast<void*>(msladdr::kAfterLastRegistrarCall) || g_registered)
        return;
    g_registered = true;
    Log("[bootstrap] detour: last registrar entered\n");
    Log("[bootstrap] detour: orig returned — builtin registry complete\n");
    // 等 managed Boot 就绪（≤10s）。Boot 只做初始化 + 起 pipe 线程，不等游戏状态 → 无死锁。
    // 最坏情况：游戏启动被 hostfxr 初始化拖慢几百毫秒（dev 组件，可接受）。
    for (int waited = 0; waited < 10000 && !g_managedReady; waited += 10) Sleep(10);
    if (g_managedReady && g_onInitGML)
    {
        g_onInitGML();
        Log("[bootstrap] natives registered inside last-registrar detour\n");
    }
    else
    {
        Log("[bootstrap] managed not ready at last registrar — natives NOT registered; sessions will be refused\n");
    }
}

// ---- BootArgs：与 MslLive.Agent 的 BootArgs struct 逐字段对应（LayoutKind.Sequential）----
// Task 13 扩尾：RegBasePtrVa/RegCountVa = 注册表记账全局 VA（Task 11 Step 5 实测；
// agent 注册表走表由它们驱动，不再用 S2 双点 AOB）。
struct BootArgs
{
    const wchar_t* gameDir;
    uint64_t functionAdd;
    uint64_t nodeSigFn;
    uint64_t execVtable;
    uint64_t regBasePtrVa;
    uint64_t regCountVa;
    int32_t regAnchorIdx1;
    int32_t regAnchorIdx2;
};
using BootFn = int32_t(*)(BootArgs*);

bool ValidatePrologues()
{
    for (const auto& c : msladdr::kChecks)
    {
        if (c.bytes[0] == 0 && c.bytes[1] == 0 && c.bytes[2] == 0 && c.bytes[3] == 0)
            continue;   // 未填校验值的条目跳过（kExecVtable 不校验，见 addresses.h）
        if (std::memcmp(reinterpret_cast<void*>(c.va), c.bytes, 16) != 0)
        {
            Log("[bootstrap] prologue mismatch @0x%llX — exe build changed, dormant\n",
                (unsigned long long)c.va);
            return false;
        }
    }
    return true;
}

std::wstring FindHostFxr()
{
    // %ProgramFiles%\dotnet\host\fxr\<最高 dotted 版本>\hostfxr.dll
    std::wstring root = L"C:\\Program Files\\dotnet\\host\\fxr";
    std::wstring best;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((root + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            if (fd.cFileName[0] == L'.') continue;
            // dotted 版本逐段数值比较
            std::wstring cand = fd.cFileName;
            auto key = [](const std::wstring& v, unsigned long long* out) {
                unsigned long long a = 0, b = 0, c = 0, d = 0;
                swscanf_s(v.c_str(), L"%llu.%llu.%llu.%llu", &a, &b, &c, &d);
                *out = (a << 48) | (b << 32) | (c << 16) | d;
            };
            unsigned long long kb = 0, kc = 0;
            key(best, &kb); key(cand, &kc);
            if (kc > kb) best = cand;
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    return best.empty() ? L"" : root + L"\\" + best + L"\\hostfxr.dll";
}

DWORD WINAPI BootstrapThread(LPVOID)
{
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring gameDir = DirOf(exePath);
    CreateDirectoryW((gameDir + L"\\msllive").c_str(), nullptr);
    g_log = _wfopen((gameDir + L"\\msllive\\bootstrap.log").c_str(), L"a");
    g_bootstrapTid = GetCurrentThreadId();
    g_crashFile = CreateFileW((gameDir + L"\\msllive\\bootstrap.log").c_str(),
                              FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    AddVectoredExceptionHandler(1, CrashVeh);   // 先挂 VEH 再走后续各步：死在哪一步一目了然
    Log("[bootstrap] attach\n");

    if (reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr)) != msladdr::kImageBase)
    { Log("[bootstrap] image base != 0x140000000 (ASLR relocated) — frozen VAs invalid, dormant\n"); return 0; }
    Log("[bootstrap] base ok\n");
    if (!ValidatePrologues()) return 0;
    Log("[bootstrap] prologues ok\n");

    if (MH_Initialize() != MH_OK) { Log("[bootstrap] MH_Initialize failed\n"); return 0; }
    Log("[bootstrap] minhook init ok\n");
    if (MH_CreateHook(reinterpret_cast<void*>(msladdr::kLastRegistrar),
                      &DetourLastRegistrar, reinterpret_cast<void**>(&g_origLastRegistrar)) != MH_OK ||
        MH_EnableHook(reinterpret_cast<void*>(msladdr::kLastRegistrar)) != MH_OK)
    { Log("[bootstrap] MinHook on last registrar failed\n"); return 0; }
    Log("[bootstrap] hook enabled\n");

    std::wstring fxrPath = FindHostFxr();
    if (fxrPath.empty()) { Log("[bootstrap] hostfxr not found (.NET 6 runtime missing?)\n"); return 0; }
    Log("[bootstrap] hostfxr: %ls\n", fxrPath.c_str());
    HMODULE fxr = LoadLibraryW(fxrPath.c_str());
    if (!fxr) { Log("[bootstrap] LoadLibrary hostfxr failed %lu\n", GetLastError()); return 0; }
    Log("[bootstrap] hostfxr loaded\n");
    auto init = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config");
    auto getDlg = (hostfxr_get_runtime_delegate_fn)GetProcAddress(fxr, "hostfxr_get_runtime_delegate");
    auto closeCtx = (hostfxr_close_fn)GetProcAddress(fxr, "hostfxr_close");
    if (!init || !getDlg || !closeCtx) { Log("[bootstrap] hostfxr exports missing\n"); return 0; }

    hostfxr_handle ctx = nullptr;
    std::wstring rtcfg = gameDir + L"\\msllive\\MslLive.Agent.runtimeconfig.json";
    int rc = init(rtcfg.c_str(), nullptr, &ctx);
    if (rc != 0 || !ctx) { Log("[bootstrap] init runtime config failed rc=0x%x\n", rc); return 0; }
    Log("[bootstrap] runtime config ok\n");
    load_assembly_and_get_function_pointer_fn loadFn = nullptr;
    rc = getDlg(ctx, hdt_load_assembly_and_get_function_pointer, (void**)&loadFn);
    closeCtx(ctx);
    if (rc != 0 || !loadFn) { Log("[bootstrap] get delegate failed rc=0x%x\n", rc); return 0; }
    Log("[bootstrap] delegate ok\n");

    std::wstring agentDll = gameDir + L"\\msllive\\MslLive.Agent.dll";
    void* bootPtr = nullptr;
    rc = loadFn(agentDll.c_str(), L"MslLive.Agent.Boot, MslLive.Agent", L"Main",
                UNMANAGEDCALLERSONLY_METHOD, nullptr, &bootPtr);
    if (rc != 0 || !bootPtr) { Log("[bootstrap] get Boot failed rc=0x%x\n", rc); return 0; }
    Log("[bootstrap] boot ptr ok\n");
    void* onInitPtr = nullptr;
    rc = loadFn(agentDll.c_str(), L"MslLive.Agent.Boot, MslLive.Agent", L"OnInitGML",
                UNMANAGEDCALLERSONLY_METHOD, nullptr, &onInitPtr);
    if (rc != 0 || !onInitPtr) { Log("[bootstrap] get OnInitGML failed rc=0x%x\n", rc); return 0; }
    Log("[bootstrap] oninit ptr ok\n");
    g_onInitGML = (OnInitGmlFn)onInitPtr;

    BootArgs args{ gameDir.c_str(), msladdr::kFunctionAdd, msladdr::kNodeSigFn,
                   msladdr::kExecVtable, msladdr::kFuncRegistryBasePtr, msladdr::kFuncRegistryCount,
                   msladdr::kRegAnchorIdx1, msladdr::kRegAnchorIdx2 };
    Log("[bootstrap] calling managed Boot\n");
    rc = ((BootFn)bootPtr)(&args);
    if (rc != 0) { Log("[bootstrap] managed Boot returned %d\n", rc); return 0; }
    g_managedReady = true;
    Log("[bootstrap] ok\n");
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(h);
        HANDLE t = CreateThread(nullptr, 0, BootstrapThread, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
