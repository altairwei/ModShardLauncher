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

        private void Server_Click(object sender, EventArgs e)
        {
            ModInterfaceServer.StartServer(1333);
            Main.Instance.Refresh();
        }
    }
}
