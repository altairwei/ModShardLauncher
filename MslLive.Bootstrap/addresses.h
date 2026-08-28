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
// 实际结构 = 顺序调 5 个注册器（0x14025A4F0×458 / 0x1401F2440×128 / 0x140248C50×70 /
// 0x140035A20×40 / 0x14020F7B0×27 站点），间插 call [rax+10h] 日志调用，全长 0x228。
// 注册器自增长函数表（分配器 0x1404CBCD0，每次容量 +=500）——
// 我方注册必须挂在 orig 返回之后（postfix），绝不能先挂。
// 定位法 = 内置名 LEA 聚类 → 两直方图目标 → 父收敛（findings-t11.md）。
inline constexpr uint64_t kInitGMLFunctions = 0x1402CDDAF;   // ← Task 11 Step 2 实测

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
    { kInitGMLFunctions,   { 0x48,0x8B,0x0D,0x3A,0x80,0x49,0x00,0x48,0x8D,0x15,0x97,0x78,0x3F,0x00,0x44,0x88 } },
    { kFunctionAdd,        { 0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57 } },
    // kExecVtable 在 .rdata/.data（vtable 是指针表不是代码），不校验字节——
    // 它的存在性由 agent 侧 NodeIndex 扫描有效性间接背书。
};
static_assert(kInitGMLFunctions != 0 && kFunctionAdd != 0, "addresses.h not measured yet");
}
