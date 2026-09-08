using ModShardLauncher.Controls;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using UndertaleModLib;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Runtime.InteropServices;
using ModShardLauncher.Mods;
using System.Diagnostics;
using UndertaleModLib.Models;

namespace ModShardLauncher
{
    /// <summary>
    /// Main.xaml 的交互逻辑
    /// </summary>
    public partial class Main : Window
    {
        public static Main Instance;
        public MainPage MainPage;
        public ModInfos ModPage;
        public ModSourceInfos ModSourcePage;
        public Settings SettingsPage;
        public static UserSettings Settings = new();
        public static LoggingLevelSwitch lls = new();
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public static IntPtr handle;
        public string mslVersion;
        public string utmtlibVersion;
        //
        private const double DefaultWidth = 960;                  // Исходная ширина
        private const double DefaultHeight = 800;                 // Исходная высота
        private const double AspectRatio = DefaultWidth / DefaultHeight; // Соотношение сторон
        private const double ScreenSizePercentage = 0.85;          // Процент от размера экрана

        public Main()
        {
            handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);

            Instance = this;
            MainPage = new MainPage();
            ModPage = new ModInfos();
            UserSettings.LoadSettings();
            ModSourcePage = new ModSourceInfos();
            if (!Directory.Exists(ModLoader.ModPath))
                Directory.CreateDirectory(ModLoader.ModPath);
            if (!Directory.Exists(ModLoader.ModSourcesPath))
                Directory.CreateDirectory(ModLoader.ModSourcesPath);

            // create File and Console (controlledby a switch) sinks
            LoggerConfiguration logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(string.Format("logs/log_{0}.txt", DateTime.Now.ToString("yyyyMMdd_HHmm")))
                .WriteTo.Logger(log => log
                    .MinimumLevel.ControlledBy(lls)
                    .WriteTo.Console()
                );

            Log.Logger = logger.CreateLogger();

            // work around to find the FileVersion of ModShardLauncher.dll for single file publishing
            // see: https://github.com/dotnet/runtime/issues/13051
            try
            {
                ProcessModule mainProcess = Msl.ThrowIfNull(Process.GetCurrentProcess().MainModule);
                string mainProcessName = Msl.ThrowIfNull(mainProcess.FileName);
                mslVersion = "v" + FileVersionInfo.GetVersionInfo(mainProcessName).FileVersion;
                utmtlibVersion = "v" + FileVersionInfo.GetVersionInfo(typeof(UndertaleCode).Assembly.Location).FileVersion;
            }
            catch (FileNotFoundException ex)
            {
                Log.Error(ex, "Cannot find the dll of ModShardLauncher");
                throw;
            }
            Log.Information("Launching msl {{{0}}} using UTMT {{{1}}}", mslVersion, utmtlibVersion);

            try
            {
                ModLoader.LoadFiles();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Something went wrong");
            }

            SettingsPage = new Settings();
            InitializeComponent();

            // Начальный размер окна
            SetInitialSize();

            Viewer.Content = MainPage;


        }

        private void SetInitialSize()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            if (screenWidth < DefaultWidth || screenHeight < DefaultHeight)
            {
                if (screenWidth < screenHeight)
                {
                    Width = screenWidth * ScreenSizePercentage;
                    Height = Width / AspectRatio; // Поддержка соотношения сторон
                }
                else
                {
                    Height = screenHeight * ScreenSizePercentage;
                    Width = Height * AspectRatio; // Поддержка соотношения сторон
                }
            }
            else
            {
                Width = DefaultWidth;
                Height = DefaultHeight;
            }
        }

        public void LogModList()
        {
            foreach (ModFile modFile in ModPage.Mods.Where(x => x.isEnabled))
            {
                string statusMessage = "";
                switch (modFile.PatchStatus)
                {
                    case PatchStatus.Patching:
                        statusMessage = "Patching failed";
                        break;

                    case PatchStatus.Success:
                        statusMessage = "Patching succeeded";
                        break;

                    case PatchStatus.None:
                        statusMessage = "Waiting to be patched";
                        break;
                }
                Log.Warning("Patching {{{2}}} for {{{0}}} {{{1}}}", modFile.Name, modFile.Version, statusMessage);
            }
        }
        private void MyToggleButton_Checked(object sender, EventArgs e)
        {
            foreach (var i in stackPanel.Children)
            {
                if (i != sender && i is MyToggleButton button)
                {
                    button.MyButton.IsChecked = false;
                }
            }
        }
        public void Refresh()
        {
            DevIndicator.Visibility = Settings.DevMode ? Visibility.Visible : Visibility.Collapsed;
            // fix #28：dev 组件自愈补装（Dev 关/dataPath 空/已装/游戏运行中 → 内部自守卫，
            // 异常只记日志——刷新路径不得被文件 IO 打崩）
            HotReload.DevMode.EnsureInstalled();
            ModPage = new ModInfos();
            ModSourcePage = new ModSourceInfos();
            Settings settingCache = new();
            settingCache.Viewer.Content = SettingsPage.Viewer.Content;
            SettingsPage = settingCache;
            try
            {
                ModLoader.LoadFiles();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Something went wrong");
            }
            if (Viewer.Content is ModInfos) Viewer.Content = ModPage;
            else if (Viewer.Content is ModSourceInfos) Viewer.Content = ModSourcePage;
            else if (Viewer.Content is Settings) Viewer.Content = SettingsPage;
            else Viewer.Content = MainPage;
        }
        private void MyToggleButton_Click(object sender, EventArgs e)
        {
            _ = Log.CloseAndFlushAsync();
            Close();
        }
        private void MyToggleButton_Click_1(object sender, EventArgs e)
        {
            if (sender is MyToggleButton button && Msl.ThrowIfNull(button.MyButton.IsChecked)) Viewer.Content = ModPage;
            else Viewer.Content = MainPage;
        }

        private void MyToggleButton_Click_2(object sender, EventArgs e)
        {
            if (sender is MyToggleButton button && Msl.ThrowIfNull(button.MyButton.IsChecked)) Viewer.Content = ModSourcePage;
            else Viewer.Content = MainPage;
        }
        private void MyToggleButton_Click_4(object sender, EventArgs e)
        {
            if (sender is MyToggleButton button && Msl.ThrowIfNull(button.MyButton.IsChecked)) Viewer.Content = SettingsPage;
            else Viewer.Content = MainPage;
        }
        private void MyToggleButton_Click_3(object sender, EventArgs e)
        {
            WindowState = WindowState.Minimized;
            if (sender is MyToggleButton button) button.MyButton.IsChecked = false;
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (HotReload.LagPrompt.NeedsPrompt(HotReload.FastPushContext.LagCount))
            {
                // [v2 Task 8] 滞后退出提示：Yes=编译并退出；No=直接退出（明示丢弃）；Cancel=中止关闭
                string msg = string.Format(
                    Application.Current.FindResource("LagExitPromptText").ToString(),
                    HotReload.FastPushContext.LagCount);
                var title = Application.Current.FindResource("LagExitPromptTitle").ToString();
                var r = MessageBox.Show(msg, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                if (r == MessageBoxResult.Yes)
                {
                    e.Cancel = true;   // 本次关闭中止——编译完再 Close() 走干净路径
                    try
                    {
                        await Controls.ModInfos.Instance.CompileDataWinFlow(useLastSavePath: true);
                    }
                    catch (Exception ex)
                    {
                        // 编译失败也照常退出（错误已由 CompileDataWinFlow 内部弹窗+日志兜底；
                        // 脏图由下次会话全量编译重建，不留滞留风险）
                        Log.Error(ex, "[fast-push] 退出前编译失败");
                    }
                    finally
                    {
                        // 修正裁决（计划 T8）：编译失败时 LagCount 仍 >0 → Close() 再触发本事件会
                        // 二次弹提示——用户在 Yes 分支已表态退出，不拦第二遍（成功路径尾部
                        // LoadFile 触发①已清零，此处赋值无害）。
                        HotReload.FastPushContext.LagCount = 0;
                        Close();
                    }
                    return;
                }
                if (r == MessageBoxResult.No)
                {
                    HotReload.FastPushContext.LagCount = 0;   // 用户明示丢弃 → 放行关闭
                }
                else
                {
                    // Cancel：中止关闭留在编辑器（计划意图「Cancel=中止关闭」——草稿漏了
                    // e.Cancel=true，只 return 不拦会直接关窗且跳过下方退出钩子）
                    e.Cancel = true;
                    return;
                }
            }
            HotReload.DevMode.OnAppExit();
            // Task 15：Dev 组件随 MSL 退出卸载（游戏运行中则留置——Installer 自己判）
            if (HotReload.DevMode.Active && !string.IsNullOrEmpty(DataLoader.dataPath))
                HotReload.DevModeInstaller.Uninstall(Path.GetDirectoryName(DataLoader.dataPath)!);
            Settings.SaveSettings();
        }
    }
    public class UserSettings
    {
        public string Language = "English";
        public bool EnableLogger = true;
        public List<string> EnableMods = new();
        // Dev 模式（热加载）——SaveSettings 序列化全部 public 字段，零额外工作
        public bool DevMode = false;
        public int LiveScriptSlots = 64;
        public int LiveShellObjects = 8;
        public int LiveEmptyRooms = 4;
        public int LiveBlankSprites = 64;
        public int LiveBlankPaths = 16;
        public string LiveShellParents = "o_button,o_button,o_menuParent,o_menuParent,,,,";
        public static void LoadSettings()
        {
            // if no settings file, early stop
            if (!File.Exists("Settings.json")) return;

            // read file
            string settings = File.ReadAllText("Settings.json");
            // convert if as UserSettings
            Main.Settings = Msl.ThrowIfNull(JsonConvert.DeserializeObject<UserSettings>(settings));

            CheckLog(Main.Settings.EnableLogger);

            // auto check active mods
            if (Main.Settings.EnableMods.Count > 0)
            {
                List<ModFile> listModFile = ModInfos.Instance.Mods;
                foreach (string i in Main.Settings.EnableMods)
                {
                    (int indexMod, ModFile? modFile) = listModFile.Enumerate().FirstOrDefault(t => t.Item2.Name == i);
                    if (modFile != null)
                        listModFile[indexMod].isEnabled = true;
                    else
                        Log.Warning($"Mod {i} not found");
                }
            }
        }
        public static void CheckLog(bool isLogConsole)
        {
            if (isLogConsole)
            {
                Main.ShowWindow(Main.handle, Main.SW_SHOW);
                Main.lls.MinimumLevel = LogEventLevel.Information;
            }
            else
            {
                Main.ShowWindow(Main.handle, Main.SW_HIDE);
                Main.lls.MinimumLevel = (LogEventLevel)1 + (int)LogEventLevel.Fatal;
            }
        }
        public static void ChangeLanguage(int index)
        {
            ResourceDictionary resDict;
            switch (index)
            {
                case 0:
                    resDict = Application.Current.Resources.MergedDictionaries.First(t => t.Source.OriginalString == @"Language/zh-cn.xaml");
                    Application.Current.Resources.MergedDictionaries.Remove(resDict);
                    Application.Current.Resources.MergedDictionaries.Add(resDict);
                    Main.Settings.Language = "Chinese";
                    break;
                case 1:
                    resDict = Application.Current.Resources.MergedDictionaries.First(t => t.Source.OriginalString == @"Language/en-us.xaml");
                    Application.Current.Resources.MergedDictionaries.Remove(resDict);
                    Application.Current.Resources.MergedDictionaries.Add(resDict);
                    Main.Settings.Language = "English";
                    break;
                case 2:
                    resDict = Application.Current.Resources.MergedDictionaries.First(t => t.Source.OriginalString == @"Language/ru-ru.xaml");
                    Application.Current.Resources.MergedDictionaries.Remove(resDict);
                    Application.Current.Resources.MergedDictionaries.Add(resDict);
                    Main.Settings.Language = "Русский";
                    break;
            }
        }
        public void SaveSettings()
        {
            File.WriteAllText("Settings.json", JsonConvert.SerializeObject(this));
        }
    }
}
