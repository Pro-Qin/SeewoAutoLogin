using Microsoft.Web.WebView2.Core;
using SeewoAutoLogin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        #region 加载 / 兜底面板

        private void ShowStatus(string title, string detail, bool busy,
            bool showRetry = false, bool showInstall = false, bool showDownload = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowStatus(title, detail, busy, showRetry, showInstall, showDownload)));
                return;
            }

            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            InstallButton.Visibility = showInstall ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
            DownloadButton.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
            StatusButtons.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = Visibility.Visible;
        }

        private void ShowStatusPanel(string title, string detail,
            bool showRetry = false, bool showInstall = false, bool showDownload = false)
            => ShowStatus(title, detail, busy: false, showRetry: showRetry, showInstall: showInstall,
                showDownload: showDownload);

        private void HideStatusPanel() => StatusPanel.Visibility = Visibility.Collapsed;

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            InstallButton.IsEnabled = false;
            try
            {
                ShowStatus("正在安装 WebView2 运行时", "正在下载并静默安装，请稍候…", busy: true);
                if (await RunInstallAsync())
                {
                    _app?.WriteDiagnosticLog("[WebView2] 运行时安装成功，重新加载界面");
                    await RetryInitializeAsync();
                }
                else
                {
                    ShowStatusPanel("WebView2 安装未成功",
                        WebView2Runtime.DescribeLegacyInstallHint() ??
                        "安装未完成。请确认网络可用，或「手动下载」官方运行时安装后再点击「重新检测」。",
                        showRetry: true, showInstall: true, showDownload: true);
                }
            }
            finally
            {
                InstallButton.IsEnabled = true;
            }
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            _autoInstallAttempted = false; // 重新检测时允许再次自动安装
            await RetryInitializeAsync();
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WebView2Runtime.ManualDownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[WebView2] 打开下载页失败: {ex.Message}");
            }
        }

        private void LogButton_Click(object sender, RoutedEventArgs e)
        {
            try { new LogViewerWindow { Owner = this }.ShowDialog(); } catch { }
        }

        #endregion
    }
}
