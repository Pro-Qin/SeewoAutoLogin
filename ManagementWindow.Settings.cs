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
        #region Settings

        private async Task SendSettings()
        {
            await SendToJs(new
            {
                type = "settings",
                passwordEnabled = _app.Config.UsePluginPassword,
                passwordSet = !string.IsNullOrEmpty(_app.Config.PluginPasswordHash),
                rotationEnabled = _app.Config.UserListRotationEnabled,
                rotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(_app.Config.UserListRotationGroupSize),
                autoStart = AutoStartService.IsEnabled,
                autoStartText = AutoStartService.DescribeState(),
                maxVisibleAccounts = PluginConfig.MaxVisibleAccounts,
                minimizeToTray = _app.Config.MinimizeToTray,
                startMinimized = _app.Config.StartMinimized,
                restoreHostsOnExit = _app.Config.RestoreHostsOnExit,
autoRepairEnabled = _app.Config.AutoRepairEnabled,
                autoShowOverlay = _app.Config.AutoShowOverlay,
                performanceMode = _app.Config.PerformanceMode,
            particleBackground = _app.Config.ParticleBackground,
                autoCheckUpdate = _app.Config.AutoCheckUpdate,
                autoInstallAfterDownload = _app.Config.AutoInstallAfterDownload
            });
            await SendStatus();
        }

        /// <summary>下发自检状态：网关 / hosts 映射 / 管理员权限 / 希沃进程 / 开机自启</summary>
        internal async Task SendStatus()
        {
            try { await SendToJs(new { type = "status", status = _app.BuildSelfCheckStatus() }); }
            catch { }
        }

        private async Task SendSeewoStatus()
        {
            try
            {
                // Process 实现 IDisposable：这里每 2 秒查一次，不释放会一直堆积到 GC 才回收
                var procs = System.Diagnostics.Process.GetProcessesByName("EasiNote");
                bool running = false;
                bool loggedIn = false;
                string hwndStr = "";
                string windowTitle = "";
                var rect = new { w = 0, h = 0 };

                try
                {
                    var proc = procs.FirstOrDefault();
                    running = proc != null;
                    if (running && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        hwndStr = proc.MainWindowHandle.ToString();
                        windowTitle = proc.MainWindowTitle;
                        User32.GetWindowRect(proc.MainWindowHandle, out RECT r);
                        rect = new { w = r.Right - r.Left, h = r.Bottom - r.Top };
                        loggedIn = IsSeewoLoggedIn();
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }

                await SendToJs(new
                {
                    type = "seewo-status",
                    running,
                    loggedIn,
                    hwnd = hwndStr,
                    title = windowTitle,
                    width = rect.w,
                    height = rect.h,
                    accounts = _app.Config.Accounts.Count,
                    active = Math.Min(PluginConfig.MaxVisibleAccounts, _app.Config.Accounts.Count),
                    lastRefresh = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                await SendToJs(new { type = "seewo-status", running = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 希沃当前算不算「已登录」。状态栏与自动遮罩必须共用这一套判据 ——
        /// 两处各写一套时结论会互相矛盾：界面显示未登录，遮罩却认为已登录而不收起来。
        /// </summary>
        private bool IsSeewoLoggedIn()
        {
            if (_app.AuthService == null) return false;
            if (!_app.Config.Accounts.Any(a => _app.AuthService.IsSessionFor(a))) return false;

            // 希沃超过 5 秒没再请求 SSO，认为它那边已经退出登录
            var last = _app.Gateway?.LastSsoRequestAtUtc;
            if (last != null && (DateTime.UtcNow - last.Value).TotalSeconds > 5) return false;
            return true;
        }

        private System.Windows.Threading.DispatcherTimer _seewoMonitorTimer;
        private void StopSeewoMonitor()
        {
            if (_seewoMonitorTimer != null)
            {
                _seewoMonitorTimer.Stop();
                _seewoMonitorTimer = null;
            }
        }

        private void StartSeewoMonitor()
        {
            StopSeewoMonitor(); // 防重复开启：窗口每次加载都重建，避免叠加多个 2s 轮询
            _seewoMonitorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _seewoMonitorTimer.Tick += async (s, e) =>
            {
                await SendSeewoStatus();

                // 自动遮罩逻辑：受全局开关 AutoShowOverlay 控制（默认关闭）。
                // 关闭时不干预手动（托盘）显示的遮罩。
                if (_app.Config.AutoShowOverlay)
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("EasiNote");
                    bool running;
                    try { var proc = procs.FirstOrDefault(); running = proc != null && proc.MainWindowHandle != IntPtr.Zero; }
                    finally { foreach (var p in procs) p.Dispose(); }

                    if (running && !IsSeewoLoggedIn())
                    {
                        // 希沃打开但未登录 → 提醒一次。已经被关掉过就不再弹：
                        // 否则用户手动关掉 2 秒后又出现，等于关不掉。
                        if (!_app.OverlayDismissedByUser &&
                            (_app.CurrentOverlay == null || !_app.CurrentOverlay.IsVisible))
                            _app.ToggleOverlay();
                    }
                    else
                    {
                        // 希沃关闭或已登录：复位标记，遮罩也收掉
                        _app.OverlayDismissedByUser = false;
                        if (_app.CurrentOverlay != null && _app.CurrentOverlay.IsVisible)
                            _app.CurrentOverlay.Close();
                    }
                }
            };
            _seewoMonitorTimer.Start();
        }

        #endregion
    }
}
