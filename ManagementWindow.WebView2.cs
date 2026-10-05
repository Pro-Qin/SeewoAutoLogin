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
        #region WebView2 初始化

        private async Task InitializeWebViewAsync()
        {
            if (_webViewInitialized) { HideStatusPanel(); return; }

            var runtimeVersion = WebView2Runtime.GetInstalledVersion();
            _app?.WriteDiagnosticLog($"[WebView2] 运行时版本: {runtimeVersion ?? "<未安装>"}");

            // 缺运行时：按既定策略自动静默安装（用户零操作），失败再给出手动入口
            if (string.IsNullOrWhiteSpace(runtimeVersion) && !await TryAutoInstallRuntimeAsync()) return;

            ShowStatus("正在加载界面…", "正在启动 WebView2 并准备前端资源…", busy: true);

            // 关键顺序：先让控件可见，再创建控制器。
            // WPF 里 Visibility=Collapsed 的元素不参与布局、也没有原生 HWND，而 WebView2 控件内部是
            // HwndHost，必须有有效父句柄才能建 CoreWebView2Controller —— 否则抛 0x80070578（无效的窗口句柄）。
            // Win7 分支锁定的 SDK 1.0.1462 对这一点比主干用的 1.0.4078 严格，所以只在 Win7 上暴露。
            // 先把底色压成深色再显示，提前可见也不会闪白；加载遮罩（StatusPanel）在更上层，照常盖住。
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 28, 28, 30);
            WebView.Visibility = Visibility.Visible;

            // 用户数据目录固定在 LOCALAPPDATA：安装到 Program Files 时也能正常创建
            var environment = await WebView2Runtime.GetEnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(environment);

            var core = WebView.CoreWebView2;
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.DOMContentLoaded += OnDomContentLoaded;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;

            try
            {
                // 前端资源落盘 + 虚拟主机映射：比 NavigateToString 拼接大字符串更快、无 2MB 上限
                var root = WebContentProvisioner.Provision();
                core.SetVirtualHostNameToFolderMapping(WebContentProvisioner.VirtualHostName, root,
                    CoreWebView2HostResourceAccessKind.Allow);
                core.Navigate(WebContentProvisioner.EntryUrl);
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[WebView2] 本地资源释放失败，回退 NavigateToString: {ex.Message}");
                core.NavigateToString(BuildInlineHtml());
            }

            _webViewInitialized = true;
        }

        /// <summary>运行时缺失时的自动静默安装（只自动尝试一次，失败后交给用户点按钮）。</summary>
        private async Task<bool> TryAutoInstallRuntimeAsync()
        {
            if (_autoInstallAttempted)
            {
                ShowStatusPanel("缺少 WebView2 运行时",
                    "未检测到 WebView2 运行时，界面无法加载。\n可点击「自动安装组件」重试，或「手动下载」安装官方运行时后点击「重新检测」。",
                    showRetry: true, showInstall: true, showDownload: true);
                return false;
            }

            _autoInstallAttempted = true;
            _app?.WriteDiagnosticLog("[WebView2] 未检测到运行时，自动开始静默安装");
            ShowStatus("缺少 WebView2 运行时组件",
                "正在自动下载并安装（官方安装器约 150KB，组件在线下载），请稍候…", busy: true);

            if (await RunInstallAsync()) return true;

            _app?.WriteDiagnosticLog("[WebView2] 自动安装未成功（已自动重试 3 次）");
            ShowStatusPanel("WebView2 运行时安装未成功",
                "已自动重试 3 次仍未成功。常见原因：网络无法访问微软下载服务器、公司网络有限制、或安装被安全软件拦截。\n\n" +
                "可以点「自动安装」再试一次；或用「手动下载」装好官方运行时后点「重新检测」。",
                showRetry: true, showInstall: true, showDownload: true);
            return false;
        }

        private async Task<bool> RunInstallAsync()
        {
            // 进度回调同时更新说明文字与进度条：下载阶段显示真实百分比，安装阶段转为不确定进度
            var progress = new Progress<Services.InstallProgress>(p =>
            {
                StatusDetail.Text = p.Text;
                if (p.Percent.HasValue)
                {
                    StatusProgress.IsIndeterminate = false;
                    StatusProgress.Value = p.Percent.Value;
                }
                else
                {
                    StatusProgress.IsIndeterminate = true;
                }
            });

            try
            {
                return await WebView2Runtime.InstallAsync(progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[WebView2] 安装运行时失败: {ex.Message}");
                return false;
            }
        }

        private async Task RetryInitializeAsync()
        {
            WebView2Runtime.ResetEnvironment();
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[WebView2] 重试初始化失败: {ex}");
                ShowStatusPanel("界面初始化失败",
                    $"WebView2 初始化失败：{ex.Message}",
                    showRetry: true, showInstall: !WebView2Runtime.IsInstalled, showDownload: true);
            }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _app?.WriteDiagnosticLog($"[WebView2] 浏览器进程异常: kind={e.ProcessFailedKind}; reason={e.Reason}; exit={e.ExitCode}");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                ShowStatusPanel("界面进程已退出",
                    "WebView2 浏览器进程意外退出，点击「重新检测」可重新加载界面。",
                    showRetry: true);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess) return;
            _app?.WriteDiagnosticLog($"[WebView2] 页面加载失败: {e.WebErrorStatus}");
            ShowStatusPanel("界面加载失败",
                $"前端资源加载失败（{e.WebErrorStatus}），点击「重新检测」重试。",
                showRetry: true);
        }

        #endregion
    }
}
