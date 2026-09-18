using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace SeewoAutoLogin
{
    public partial class WelcomeWindow : Window
    {
        private readonly App _app;

        public bool AgreementAccepted { get; private set; }

        /// <summary>欢迎界面里用户选择的开机自启状态</summary>
        public bool AutoStartEnabled => AutoStartCheckBox?.IsChecked == true;

        /// <summary>把用户协议正文填进协议页（与「关于」页的弹窗共用同一份嵌入资源）</summary>
        private void LoadAgreementText()
        {
            try
            {
                var text = App.LoadTermsText();
                if (AgreementText == null) return;
                AgreementText.Text = string.IsNullOrWhiteSpace(text)
                    ? "协议正文载入失败，请前往「关于 → 用户协议」查看。"
                    : text;
            }
            catch
            {
                // 协议载入失败不应阻止用户进入软件
            }
        }

        public WelcomeWindow()
        {
            try
            {
                _app = (App)Application.Current;
                InitializeComponent();
                try { Icon = App.LoadWindowIcon(); } catch { }
                InitializeAutoStartOption();
                LoadAgreementText();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"欢迎窗口初始化失败：{ex.Message}", "希沃自动登录",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        /// <summary>
        /// 初始化开机自启选项：注册表里已经有这项设置就按实际状态显示，
        /// 否则按配置显示（首次安装默认勾选——托盘常驻才能让希沃快捷登录窗口随时可用）。
        /// </summary>
        private void InitializeAutoStartOption()
        {
            try
            {
                // 只反映系统里的真实状态：装了自启就勾上，没装就不勾。
                // （此前是「系统已启用 || 配置默认值」，而配置默认值为 true，导致这里永远勾选。）
                AutoStartCheckBox.IsChecked = Services.AutoStartService.IsEnabled;
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 初始化开机自启选项失败: {ex.Message}");
            }

            UpdateAutoStartHint();
        }

        private void AutoStart_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAutoStartHint();
        }

        private void UpdateAutoStartHint()
        {
            try
            {
                // XAML 解析期间 Checked 事件可能早于提示控件创建，这里做保护
                if (AutoStartStateText == null) return;

                var modeText = Services.AutoStartService.IsAdministrator()
                    ? "计划任务·最高权限，开机自动运行且不再弹 UAC"
                    : "注册表启动项，开机首次启动需要授权一次";
                AutoStartStateText.Text = AutoStartCheckBox.IsChecked == true
                    ? $"当前：将开启开机自启（{modeText}；点击「开始使用」后生效，之后可在「设置 → 常规」中修改）"
                    : "当前：不开机自启（仍可手动打开本程序，之后可在「设置 → 常规」中修改）";
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 刷新开机自启提示失败: {ex.Message}");
            }
        }

        /// <summary>把欢迎界面的选择写入系统（优先最高权限计划任务，失败回退注册表启动项）与配置。</summary>
        private void ApplyAutoStartChoice()
        {
            try
            {
                var enabled = AutoStartCheckBox.IsChecked == true;
                if (_app?.Config != null) _app.Config.AutoStartEnabled = enabled;

                if (enabled)
                {
                    var ok = Services.AutoStartService.Enable(out var error, out var mode);
                    _app?.WriteDiagnosticLog($"[FirstLaunch] 开机自启: {(ok ? "已开启" : "开启失败")}; mode={mode}; {error}");
                    if (!ok) _app?.NotifyInfo("开机自启未开启", "写入开机自启失败：" + error);
                    else if (!string.IsNullOrEmpty(error)) _app?.WriteDiagnosticLog($"[FirstLaunch] {error}");
                }
                else
                {
                    var ok = Services.AutoStartService.Disable(out var error);
                    _app?.WriteDiagnosticLog($"[FirstLaunch] 开机自启: {(ok ? "已关闭" : "关闭失败")}; {error}");
                }
                _app?.SaveConfig();
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 应用开机自启设置失败: {ex.Message}");
            }
        }

        private void Tab_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                // XAML 解析期间该事件可能早于控件创建触发，必须判空（否则每次启动都会记录一次异常日志）
                if (AgreementPage == null || CreditsPage == null || GuidePage == null) return;

                AgreementPage.Visibility = Visibility.Collapsed;
                CreditsPage.Visibility = Visibility.Collapsed;
                GuidePage.Visibility = Visibility.Collapsed;

                var tag = (sender as System.Windows.Controls.RadioButton)?.Tag as string;
                switch (tag)
                {
                    case "agreement":
                        AgreementPage.Visibility = Visibility.Visible;
                        break;
                    case "credits":
                        CreditsPage.Visibility = Visibility.Visible;
                        break;
                    case "guide":
                        GuidePage.Visibility = Visibility.Visible;
                        break;
                }
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 页面切换异常: {ex.Message}");
            }
        }

        private void Agree_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (StartButton == null || AgreeCheckBox == null) return;
                StartButton.IsEnabled = AgreeCheckBox.IsChecked == true;
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 同意状态变更异常: {ex.Message}");
            }
        }

        /// <summary>询问是否观看使用教程；选择结果由主界面加载后自动播放</summary>
        private void AskAboutTour()
        {
            try
            {
                var wantTour = MessageBox.Show(
                    "要不要花 30 秒看一遍使用教程？\n\n" +
                    "  · 是   → 进入主界面后自动演示：账号在哪里加、加完希沃会变成什么样\n" +
                    "  · 否   → 直接进入主界面（之后可在「设置 → 帮助与维护」里重看）",
                    "使用教程", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

                if (_app?.Config != null)
                {
                    _app.Config.PendingTour = wantTour;
                    _app.Config.TourCompleted = !wantTour;
                }
                _app?.WriteDiagnosticLog($"[Welcome] 教程选择: {(wantTour ? "查看" : "跳过")}");
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 询问教程失败: {ex.Message}");
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyAutoStartChoice();
                AskAboutTour();
                AgreementAccepted = true;
                DialogResult = true;
                // Setting DialogResult automatically closes the window
            }
            catch (Exception ex)
            {
                MessageBox.Show($"关闭窗口失败：{ex.Message}", "希沃自动登录",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 如果用户点了 X 而不是"开始使用"，视为拒绝协议
            if (!AgreementAccepted)
            {
                var result = MessageBox.Show(
                    "您尚未同意用户协议，应用将退出。下次启动时可重新查看协议。",
                    "希沃自动登录",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开链接：{ex.Message}", "希沃自动登录",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
