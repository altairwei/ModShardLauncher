using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ModShardLauncher.Mods;
using Serilog;

namespace ModShardLauncher.Controls
{
    /// <summary>
    /// ModInfos.xaml 的交互逻辑
    /// </summary>
    public partial class ModInfos : UserControl
    {
        public static ModInfos Instance;
        public List<ModFile> Mods { get; set; } = new();
        public ModInfos()
        {
            InitializeComponent();
            Instance = this;
            // [v2 Task 7] 快推按钮只随 Dev 模式出现（Main.Refresh 重建 ModInfos → ctor 再跑 → 跟随设置；
            // Release 默认 DevMode=false → Collapsed，视觉零差）
            FastPushButton.Visibility = HotReload.DevMode.Active ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void Open_Click(object sender, EventArgs e)
        {
            await DataLoader.DoOpenDialog();
            Main.Instance.Refresh();
        }
        private async void Save_Click(object sender, EventArgs e) => await CompileDataWinFlow(false);

        internal async Task CompileDataWinFlow(bool useLastSavePath)
        {
            if (DataLoader.data.FORM == null)
            {
                MessageBox.Show(Application.Current.FindResource("LoadDataWarning").ToString());
                return;
            }

            // [v2 Task 6] 裁决 9：快推让工作图带漂移后，全量编译必须先回精源（spec §4）——
            // 否则 PatchFile 重放在漂移图上进行（文本链错基 + 双重补丁 + 缓存面与图失配）。
            // 无快推过的会话恒 false → 零成本跳过；LoadFile 尾部顺带触发①清账本/换缓存——
            // 全量编译本就要走到。
            if (HotReload.FastPushCore.NeedsPristineReload)
                await DataLoader.LoadFile(DataLoader.dataPath, true);

            try
            {
                bool patchSucess = false;

                try
                {
                    ModLoader.PatchFile();
                    Log.Information("Successfully patch vanilla");
                    patchSucess = true;
                    Main.Instance.LogModList();
                }
                catch(Exception ex)
                {
                    Main.Instance.LogModList();
                    Log.Error(ex, "Something went wrong");
                    Log.Information("Failed patching vanilla");
                    MessageBox.Show(ex.ToString(), Application.Current.FindResource("SaveDataWarning").ToString());
                }

                // attempt to save the patched data
                if (patchSucess)
                {
                    bool saved;
                    if (useLastSavePath && !string.IsNullOrEmpty(DataLoader.savedDataPath))
                    {
                        await DataLoader.SaveFile(DataLoader.savedDataPath);
                        saved = true;
                    }
                    else
                    {
                        saved = await DataLoader.DoSaveDialog();
                    }
                    if (saved)
                    {
                        // copy the dataloot.json in the stoneshard directory
                        LootUtils.SaveLootTables(Msl.ThrowIfNull(Path.GetDirectoryName(DataLoader.savedDataPath)));
                        // 双输出（spec §4.4）：写盘已完成 → 热推；热通道失败不影响写盘结果
                        HotReload.DevMode.ReportResult(HotReload.HotPipeline.BuildAndPush(DataLoader.data, DataLoader.savedDataPath));
                    }
                    else Log.Information("Saved cancelled.");
                }

                // reload the data
                await DataLoader.LoadFile(DataLoader.dataPath, true);
                Main.Instance.Refresh();
            }
            catch (Exception ex)
            {
                // fix #31：本流程经 SourceBar 以 fire-and-forget 调起（_ = CompileDataWinFlow），
                // 未捕获异常会无声蒸发并吞掉尾部 vallina 重载——基底变脏，下轮编译报
                // o_msl_timer already exists（09-04 20:26 次生形态）。兜底：可见 + 带栈落日志，
                // 并如实提示基底可能未重置的恢复手段。
                Log.Error(ex, "[compile-flow] 编译流程未捕获异常（尾部 vallina 重载未执行——编译基底可能脏）");
                MessageBox.Show(ex.ToString() +
                    "\n\n编译基底可能未重置：若下轮编译报 o_msl_timer already exists，重启 MSL 即可恢复。",
                    Application.Current.FindResource("SaveDataWarning").ToString());
            }
        }

        /// <summary>热更三态反馈行（spec §8；Dev 关时 ReportResult 不走到这里）。</summary>
        public void SetLiveStatus(string msg)
        {
            if (LiveStatus != null) LiveStatus.Text = msg;
        }

        // [v2 Task 7] 快推按钮：同步调 RunPush（轮内是 CPU 工作，无 async 面）+ 状态行三态反馈。
        // 状态行文案沿用 v1 硬编码中文先例（ReportResult 同款）；按钮 ToolTip 走 Language 键。
        private void FastPush_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = HotReload.FastPushCore.RunPush();
                sw.Stop();
                SetFastPushStatus(r, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)   // #31 兜底同款：带栈落日志 + 状态行
            {
                Log.Error(ex, "[fast-push] 未捕获异常");
                SetLiveStatus("快推异常：" + ex.GetType().Name);
            }
        }

        void SetFastPushStatus(HotReload.FastPushOutcome r, long ms)
        {
            if (r.Rejected)
            {
                SetLiveStatus("快推拒绝：" + r.RejectionReason);
                return;
            }
            string msg = r.Succeeded
                ? $"快推成功：编 {r.CompiledEntries} entry，推 {r.PushedEntries} entry，{ms} ms"
                : $"快推失败：{string.Join("；", r.Failures)}";
            if (r.LagEntries > 0) msg += $"｜内存领先磁盘 {r.LagEntries} 处";
            SetLiveStatus(msg);
            Log.Information("[fast-push] {msg}", msg);
        }

        private void Server_Click(object sender, EventArgs e)
        {
            ModInterfaceServer.StartServer(1333);
            Main.Instance.Refresh();
        }
    }
}
