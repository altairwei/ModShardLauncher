using ModShardLauncher.Mods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UndertaleModTool;

namespace ModShardLauncher.Controls
{
    /// <summary>
    /// GeneralPage.xaml 的交互逻辑
    /// </summary>
    public partial class GeneralPage : UserControl
    {
        public GeneralPage()
        {
            InitializeComponent();

            // add Languages
            Languages.Add("中文");
            Languages.Add("English");
            //Languages.Add("Русский");

            switch (Main.Settings.Language)
            {
                case "Chinese":
                    LangSelector.SelectedIndex = 0;
                    UserSettings.ChangeLanguage(0);
                    break;
                case "English":
                    LangSelector.SelectedIndex = 1;
                    UserSettings.ChangeLanguage(1);
                    break;
                case "Russian":
                    LangSelector.SelectedIndex = 2;
                    UserSettings.ChangeLanguage(2);
                    break;
            }

            InitDevBlock();
        }
        public int selectIndex { get; set; } = 1;
        public List<string> Languages { get; set; } = new List<string>();
        public int selection = -1;

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (selection == -1)
            {
                selection = LangSelector.SelectedIndex;
                return;
            }
            ComboBox combo = Msl.ThrowIfNull(sender as ComboBox);
            UserSettings.ChangeLanguage(combo.SelectedIndex);
            selection = combo.SelectedIndex;
            LangSelector.SelectedIndex = selection;
        }

        private void Logger_Checked(object sender, RoutedEventArgs e)
        {
            Main.Settings.EnableLogger = Msl.ThrowIfNull(Logger.IsChecked);
            UserSettings.CheckLog(Main.Settings.EnableLogger);
            Main.Settings.SaveSettings();
        }

        void InitDevBlock()
        {
            DevModeToggle.IsChecked = Main.Settings.DevMode;
            LiveScriptSlotsBox.Text = Main.Settings.LiveScriptSlots.ToString();
            LiveShellObjectsBox.Text = Main.Settings.LiveShellObjects.ToString();
            LiveEmptyRoomsBox.Text = Main.Settings.LiveEmptyRooms.ToString();
            LiveBlankSpritesBox.Text = Main.Settings.LiveBlankSprites.ToString();
            LiveBlankPathsBox.Text = Main.Settings.LiveBlankPaths.ToString();
            LiveShellParentsBox.Text = Main.Settings.LiveShellParents;
        }

        private void DevModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            Main.Settings.DevMode = DevModeToggle.IsChecked == true;
            Main.Settings.SaveSettings();
        }

        int CurrentQuota(TextBox box)
        {
            if (box == LiveScriptSlotsBox) return Main.Settings.LiveScriptSlots;
            if (box == LiveShellObjectsBox) return Main.Settings.LiveShellObjects;
            if (box == LiveEmptyRoomsBox) return Main.Settings.LiveEmptyRooms;
            if (box == LiveBlankSpritesBox) return Main.Settings.LiveBlankSprites;
            return Main.Settings.LiveBlankPaths;
        }

        private void QuotaBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = Msl.ThrowIfNull(sender as TextBox);
            if (int.TryParse(box.Text, out int v) && v >= 0)
            {
                if (box == LiveScriptSlotsBox) Main.Settings.LiveScriptSlots = v;
                else if (box == LiveShellObjectsBox) Main.Settings.LiveShellObjects = v;
                else if (box == LiveEmptyRoomsBox) Main.Settings.LiveEmptyRooms = v;
                else if (box == LiveBlankSpritesBox) Main.Settings.LiveBlankSprites = v;
                else if (box == LiveBlankPathsBox) Main.Settings.LiveBlankPaths = v;
                Main.Settings.SaveSettings();
            }
            else box.Text = CurrentQuota(box).ToString();   // 解析失败恢复原值
        }

        private void ShellParentsBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Main.Settings.LiveShellParents = LiveShellParentsBox.Text;
            Main.Settings.SaveSettings();
        }
    }
}
