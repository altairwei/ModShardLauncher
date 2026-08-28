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
