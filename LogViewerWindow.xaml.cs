using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace SeewoAutoLogin
{
    public partial class LogViewerWindow : Window
    {
        public LogViewerWindow()
        {
            InitializeComponent();
            LoadLog();
        }

        private void LoadLog()
        {
            try
            {
                var logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeewoAutoLogin", "Logs");
                var todayLog = Path.Combine(logsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                if (File.Exists(todayLog))
                {
                    var lines = File.ReadAllLines(todayLog);
                    LogContent.Text = string.Join("\n", lines.Reverse().Take(500).Reverse());
                    LogStatus.Text = $"共 {lines.Length} 行，显示最新 500 行";
                }
                else
                {
                    LogContent.Text = "暂无今日日志。";
                    LogStatus.Text = "";
                }
            }
            catch (Exception ex)
            {
                LogContent.Text = $"读取日志失败: {ex.Message}";
            }
        }

        private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLog();

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeewoAutoLogin", "Logs");
                var todayLog = Path.Combine(logsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (File.Exists(todayLog))
                {
                    File.WriteAllText(todayLog, "");
                    LogContent.Text = "日志已清空。";
                    LogStatus.Text = "";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清空失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
