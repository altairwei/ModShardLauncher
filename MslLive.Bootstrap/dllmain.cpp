// MslLive.Bootstrap — version.dll 代理 + hostfxr 拉起 MslLive.Agent。
// 安全总线：任一步失败 → 记日志 + 返回（游戏继续原生运行；stub 无 agent 无害休眠）。
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
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

// ---- InitGMLFunctions hook：在注册窗口内（游戏线程、原函数返回后）调 managed 注册 ----
using InitGmlFn = void(*)();
InitGmlFn g_origInitGML = nullptr;
using OnInitGmlFn = void(*)();
OnInitGmlFn g_onInitGML = nullptr;
volatile bool g_managedReady = false;

void DetourInitGML()
{
    g_origInitGML();
    // 等 managed Boot 就绪（≤10s）。Boot 只做初始化 + 起 pipe 线程，不等游戏状态 → 无死锁。
    // 最坏情况：游戏启动被 hostfxr 初始化拖慢几百毫秒（dev 组件，可接受）。
    for (int waited = 0; waited < 10000 && !g_managedReady; waited += 10) Sleep(10);
    if (g_managedReady && g_onInitGML)
    {
        g_onInitGML();
        Log("[bootstrap] natives registered inside InitGML detour\n");
    }
    else
    {
        Log("[bootstrap] managed not ready at InitGML — natives NOT registered; sessions will be refused\n");
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
    Log("[bootstrap] attach\n");

    if (reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr)) != msladdr::kImageBase)
    { Log("[bootstrap] image base != 0x140000000 (ASLR relocated) — frozen VAs invalid, dormant\n"); return 0; }
    if (!ValidatePrologues()) return 0;

    if (MH_Initialize() != MH_OK) { Log("[bootstrap] MH_Initialize failed\n"); return 0; }
    if (MH_CreateHook(reinterpret_cast<void*>(msladdr::kInitGMLFunctions),
                      &DetourInitGML, reinterpret_cast<void**>(&g_origInitGML)) != MH_OK ||
        MH_EnableHook(reinterpret_cast<void*>(msladdr::kInitGMLFunctions)) != MH_OK)
    { Log("[bootstrap] MinHook on InitGMLFunctions failed\n"); return 0; }

    std::wstring fxrPath = FindHostFxr();
    if (fxrPath.empty()) { Log("[bootstrap] hostfxr not found (.NET 6 runtime missing?)\n"); return 0; }
    HMODULE fxr = LoadLibraryW(fxrPath.c_str());
    if (!fxr) { Log("[bootstrap] LoadLibrary hostfxr failed %lu\n", GetLastError()); return 0; }
    auto init = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config");
    auto getDlg = (hostfxr_get_runtime_delegate_fn)GetProcAddress(fxr, "hostfxr_get_runtime_delegate");
    auto closeCtx = (hostfxr_close_fn)GetProcAddress(fxr, "hostfxr_close");
    if (!init || !getDlg || !closeCtx) { Log("[bootstrap] hostfxr exports missing\n"); return 0; }

    hostfxr_handle ctx = nullptr;
    std::wstring rtcfg = gameDir + L"\\msllive\\MslLive.Agent.runtimeconfig.json";
    int rc = init(rtcfg.c_str(), nullptr, &ctx);
    if (rc != 0 || !ctx) { Log("[bootstrap] init runtime config failed rc=0x%x\n", rc); return 0; }
    load_assembly_and_get_function_pointer_fn loadFn = nullptr;
    rc = getDlg(ctx, hdt_load_assembly_and_get_function_pointer, (void**)&loadFn);
    closeCtx(ctx);
    if (rc != 0 || !loadFn) { Log("[bootstrap] get delegate failed rc=0x%x\n", rc); return 0; }

    std::wstring agentDll = gameDir + L"\\msllive\\MslLive.Agent.dll";
    void* bootPtr = nullptr;
    rc = loadFn(agentDll.c_str(), L"MslLive.Agent.Boot, MslLive.Agent", L"Main",
                UNMANAGEDCALLERSONLY_METHOD, nullptr, &bootPtr);
    if (rc != 0 || !bootPtr) { Log("[bootstrap] get Boot failed rc=0x%x\n", rc); return 0; }
    void* onInitPtr = nullptr;
    rc = loadFn(agentDll.c_str(), L"MslLive.Agent.Boot, MslLive.Agent", L"OnInitGML",
                UNMANAGEDCALLERSONLY_METHOD, nullptr, &onInitPtr);
    if (rc != 0 || !onInitPtr) { Log("[bootstrap] get OnInitGML failed rc=0x%x\n", rc); return 0; }
    g_onInitGML = (OnInitGmlFn)onInitPtr;

    BootArgs args{ gameDir.c_str(), msladdr::kFunctionAdd, msladdr::kNodeSigFn,
                   msladdr::kExecVtable, msladdr::kFuncRegistryBasePtr, msladdr::kFuncRegistryCount,
                   msladdr::kRegAnchorIdx1, msladdr::kRegAnchorIdx2 };
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
