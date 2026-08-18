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

        public WelcomeWindow()
        {
            try
            {
                _app = (App)Application.Current;
                InitializeComponent();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"欢迎窗口初始化失败：{ex.Message}", "希沃自动登录",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void Tab_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
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
                StartButton.IsEnabled = AgreeCheckBox.IsChecked == true;
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Welcome] 同意状态变更异常: {ex.Message}");
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
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
