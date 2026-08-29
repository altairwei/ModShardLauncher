#pragma once
// StoneShard.exe 热加载地址表（Task 11 实测；imageBase=0x140000000，文件 VA == 运行时 VA，S1⑥）。
// exe 更新 → 全部重测；启动期 prologue 校验不过即休眠（dllmain.cpp），绝不带病运行。
#include <cstdint>
namespace msladdr {

inline constexpr uint64_t kImageBase  = 0x140000000;
inline constexpr uint64_t kMmAlloc    = 0x140506D00;   // S1：4 站点收敛
inline constexpr uint64_t kMmFree     = 0x1404CBAF0;   // S1：唯一命中

// 节点类型描述符（S2 的 "Code 节点 +0x00 固定值"；Task 11 复核语义修正）：
// 该 VA 不是代码而是 .rdata 记录 { +0x00 handler=0x140277A70（ctor：mov [rcx],本描述符），
// +0x08 inline name "<unknown>" }——节点对象是 C++ 实例，+0x00 即其 vtable/型别描述符。
// 校验按数据身份处理（前 8 字节 = build 相关的绝对指针，更新即变，足够做身份锚）。
inline constexpr uint64_t kNodeSigFn  = 0x1406BE508;
inline constexpr uint64_t kExecVtable = 0x14066AC48;   // S2/S3：执行记录 vtable

// InitGMLFunctions：内置函数注册编排器（Task 11 Step 2 实测）。
// Task 16 真机复盘修正：0x1402CDDAF 是编排器 F（0x1402CDC60..0x1402CDFD7，
// prologue push r14; sub rsp,0C30h）的函数中部 merge 标签（je@0x1402CDCC3 /
// jne@0x1402CDCCF / 直落三路汇入），不是函数入口——不可 hook（入口 rsp≡0
// mod 16 → CRT #GP；detour 栈消耗平移 F 的 rsp 相对访问并错位 epilogue）。
// 保留作 build fingerprint 锚。
inline constexpr uint64_t kInitGMLFunctions = 0x1402CDDAF;   // ← Task 11 Step 2 实测

// F 内最后一个注册器（Task 16 fix-loop #3 实测选定的新 hook 点）。
// 0x1403148A0 = 注册表自增长/追加函数本体：读 count(0x140A34254)/capacity
// (0x140A474F0)/base(0x140A34468) 三全局，容量不足则 +500 走 realloc
// (0x1404CBCD0)，记录步进 0x50 追加。正规 ABI 函数：prologue sub rsp,38h、
// epilogue add rsp,38h; ret（@0x1403155FD），全 .text 唯一 E8 调用者 =
// F 内 call @0x1402CDFB4。E8 调用保证入口 rsp≡8 mod 16，无需对齐垫片；
// F 在 call 返回后不读 rax（rcx/rdx/rax 全部重载，r14 由 epilogue pop 恢复）
// → detour 可自由使用易失寄存器。orig 返回 = 注册表已完整、仍在 F 内游戏线程上。
inline constexpr uint64_t kLastRegistrar          = 0x1403148A0;  // ← hook 目标
inline constexpr uint64_t kLastRegistrarCallSite  = 0x1402CDFB4;  // 唯一 E8 调用指令
inline constexpr uint64_t kAfterLastRegistrarCall = 0x1402CDFB9;  // 该 call 的返回地址（RA 门控值）

// Function_Add：注册器体内 723 站点收敛的 call 目标（5 注册器共用）。
// 参数序（Step 3 实测，3 个站点一致）：rcx=name(.rdata), rdx=funcptr(.text), r8d=argc
// —— 3 参形态，无 r9 flag。typedef：
//   extern "C" void __cdecl Function_Add(const char* name, void* funcptr, int argc);
// TRoutine ABI（Step 3 实测 draw_sprite @0x1401EBA90）：
//   result=rcx, self=rdx, other=r8, argc=r9d, args=入口第 5 个栈参 [rsp+0x28]。
//   读参助手（可选）：0x140273990 (rcx=args, edx=idx) → int eax；
//                    0x140273E90 (rcx=args, edx=idx) → real xmm0。
inline constexpr uint64_t kFunctionAdd = 0x140273220;        // ← Task 11 Step 3 实测

// 内置变量注册表（Task 11 Step 4 实测 + Step 5 活体走表全量复核；record = base + smallId*0x20）：
//   +0x00 namePtr（堆内拷贝）  +0x08 getter  +0x10 setter  +0x18 flag 字节
// smallId = 注册序（fn1 0..90 → fn2 链 91..217 → rollback 族 218..223，共 224 条，
// 见 BuiltinVars.json；rollback 族注册者 0x140315610 无 E8 调用者——间接/尾跳入口，活体实证）。
// agent 侧 var 表活体校准直接走本表，不必信模拟器绝对值。
inline constexpr uint64_t kVarTableBase  = 0x1407EDAA0;
inline constexpr uint64_t kVarCount      = 0x1407F1920;   // 全局计数（写完 = 224）
inline constexpr int      kVarRecordStride = 0x20;
// fn2 入口把当时计数存这里（= 实例变量区结束界）：活体期望值 91，自检锚。
inline constexpr uint64_t kVarFn2BaseCheckpoint = 0x1407ED688;

// 函数注册表（Step 5 活体全量走表修正 S2 误读；2535 条，0 坏记录）：
//   base = [kFuncRegistryBasePtr]（全局指针，指向记录 0）  count = [kFuncRegistryCount]
//   record 步进 0x50，布局 = { +0x00 内联名(≤0x40；恰好占满则无 NUL，实测
//   switch_irsensor_moment_config_set_preprocess_intensity_threshold=64 字符),
//   +0x40 funcptr, +0x48 argc(i32, -1=变参), +0x4C 0xFFFFFFFF }
// 索引跨会话稳定（同 exe build）：sprite_exists=645、object_get_name=761、record 0=camera_create。
// 字节码 call.v 内置函数操作数 = 本表原始索引（无偏置，top 0x00；
// 活体双验：ds_map_find_first=1273、ds_map_size=1253，均按名命中本表同槽）。
// agent 引导 = 读两个全局 → 走 count 条记录 → 建 名→(index,funcptr,argc) 映射；无需 AOB。
inline constexpr uint64_t kFuncRegistryBasePtr = 0x140A34468;  // ← Step 5 实测
inline constexpr uint64_t kFuncRegistryCount   = 0x140A34254;  // ← Step 5 实测
inline constexpr int      kFuncRecordStride    = 0x50;
inline constexpr int kRegAnchorIdx1 = 645;   // sprite_exists
inline constexpr int kRegAnchorIdx2 = 761;   // object_get_name
inline constexpr const char* kRegAnchorName1 = "sprite_exists";
inline constexpr const char* kRegAnchorName2 = "object_get_name";

struct PrimCheck { uint64_t va; uint8_t bytes[16]; };
inline constexpr PrimCheck kChecks[] = {
    { kMmAlloc,            { 0x48,0x8B,0xC1,0x4C,0x8D,0x15,0xF6,0x92,0xAF,0xFF,0x49,0x83,0xF8,0x0F,0x0F,0x87 } },
    { kMmFree,             { 0x48,0x85,0xC9,0x0F,0x84,0x00,0x01,0x00,0x00,0x48,0x89,0x5C,0x24,0x08,0x57,0x48 } },
    { kNodeSigFn,          { 0x70,0x7A,0x27,0x40,0x01,0x00,0x00,0x00,0x3C,0x75,0x6E,0x6B,0x6E,0x6F,0x77,0x6E } }, // 数据身份，见上注释
    { kInitGMLFunctions,   { 0x48,0x8B,0x0D,0x3A,0x80,0x49,0x00,0x48,0x8D,0x15,0x97,0x78,0x3F,0x00,0x44,0x88 } }, // fingerprint（merge 标签，非 hook 点，见上）
    { kLastRegistrar,      { 0x48,0x83,0xEC,0x38,0x8B,0x05,0xAA,0xF9,0x71,0x00,0x8B,0x15,0x40,0x2C,0x73,0x00 } },
    // call 站点校验：E8 rel32=0x000468E7（0x1402CDFB9+0x468E7=0x1403148A0）+ 其后
    // F 收尾指令（mov rcx,[rip] / lea rdx,[rip]）——三者同时锁死调用点与目标。
    { kLastRegistrarCallSite, { 0xE8,0xE7,0x68,0x04,0x00,0x48,0x8B,0x0D,0x30,0x7E,0x49,0x00,0x48,0x8D,0x15,0x85 } },
    { kFunctionAdd,        { 0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57 } },
    // kExecVtable 在 .rdata/.data（vtable 是指针表不是代码），不校验字节——
    // 它的存在性由 agent 侧 NodeIndex 扫描有效性间接背书。
};
static_assert(kInitGMLFunctions != 0 && kFunctionAdd != 0 && kLastRegistrar != 0,
              "addresses.h not measured yet");
}
