using System.Windows;
using System.Windows.Input;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 自绘标题栏（最小化 / 最大化 / 关闭 + 拖动 + 双击最大化）。
    ///
    /// 默认用自绘：Windows 的系统标题栏是浅色直角，跟这套深色界面放一起很割裂。
    /// 设置里可以切回系统标题栏；Win7 强制使用系统标题栏 —— 那边 Aero 与 WindowChrome 的
    /// 组合问题太多，不值得为了一致性去踩。
    /// </summary>
    public partial class ManagementWindow
    {
        private bool _useSystemTitleBar;

        private void InitializeWindowChrome()
        {
            var forcedByOs = Services.WebView2Runtime.IsLegacyWindows;
            _useSystemTitleBar = forcedByOs || _app?.Config?.UseSystemTitleBar == true;
            ApplyWindowChrome();
            StateChanged += (_, _) => UpdateMaximizeGlyph();
            UpdateMaximizeGlyph();
        }

        private void ApplyWindowChrome()
        {
            try
            {
                if (_useSystemTitleBar)
                {
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    if (TitleBar != null) TitleBar.Visibility = Visibility.Collapsed;
                    System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
                }
                else
                {
                    WindowStyle = WindowStyle.None;
                    if (TitleBar != null) TitleBar.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                // 切不过去就退回系统标题栏：总比一个没有边框、拖不动的窗口强
                _app?.WriteDiagnosticLog($"[Chrome] 应用窗口边框失败: {ex.GetType().Name} - {ex.Message}");
                try
                {
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    if (TitleBar != null) TitleBar.Visibility = Visibility.Collapsed;
                }
                catch { }
            }
        }

        private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeGlyph();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        }

        /// <summary>最大化图标在「□」与「还原」之间切换（同一套 Segoe 字体图标）。</summary>
        private void UpdateMaximizeGlyph()
        {
            if (MaxButton == null) return;
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            if (MaxButton.ToolTip is string) MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }
}
