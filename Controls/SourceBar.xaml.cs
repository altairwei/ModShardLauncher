using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ModShardLauncher.Controls
{
    /// <summary>
    /// SourceBar.xaml 的交互逻辑
    /// </summary>
    public partial class SourceBar : UserControl
    {
        public SourceBar()
        {
            InitializeComponent();
        }

        private void CompileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ModSource source = Msl.ThrowIfNull(DataContext as ModSource);
                bool packed = UtilsPacker.Pack(source.Path);
                if (ModShardLauncher.HotReload.DevMode.Active)
                {
                    if (packed)
                    {
                        // 当轮消费刚打包的 .sml（dev 链专用）：编译用的程序集缓存在 LoadFiles
                        // （仅启动与编译收尾 Refresh 时重读），不在此重读则 PatchFile 用的仍是
                        // 上一轮缓存程序集——新 .sml 要下一次点击才生效（「编译两次」舞步）。
                        // 经典 Save 入口保持 MSL 原语义不动。
                        ModLoader.LoadFiles();
                        _ = ModInfos.Instance.CompileDataWinFlow(true);   // 一键串联（spec §4.3）：打包 → 编译 → 热推
                    }
                    else
                    {
                        // 守卫（fail-closed）：Pack 失败（含 FilePacker 内部吞掉的异常，均归 false）
                        // 时 .sml 还是旧的，继续编译 = 静默热推旧货且看似成功——中止并明示
                        Log.Error("Dev 一键链中止：打包失败 {{{0}}}", source.Name);
                        MessageBox.Show(Application.Current.FindResource("PackFailedWarning").ToString(), source.Name);
                    }
                }
            }
            catch(Exception ex)
            {
                Log.Error(ex, "Something went wrong");
            }
            
            Msl.ThrowIfNull(Main.Instance.Viewer.Content as UserControl).UpdateLayout();
            Main.Instance.Refresh();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("explorer.exe", Msl.ThrowIfNull(DataContext as ModSource).Path);
            
        }
    }
}
