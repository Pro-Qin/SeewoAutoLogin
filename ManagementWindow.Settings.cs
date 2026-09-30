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
                autoShowOverlay = _app.Config.AutoShowOverlay,
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
                var proc = System.Diagnostics.Process.GetProcessesByName("EasiNote").FirstOrDefault();
                bool running = proc != null;
                bool loggedIn = false;
                string hwndStr = "";
                string windowTitle = "";
                var rect = new { w = 0, h = 0 };

                if (running && proc.MainWindowHandle != IntPtr.Zero)
                {
                    hwndStr = proc.MainWindowHandle.ToString();
                    windowTitle = proc.MainWindowTitle;
                    User32.GetWindowRect(proc.MainWindowHandle, out RECT r);
                    rect = new { w = r.Right - r.Left, h = r.Bottom - r.Top };
                    loggedIn = _app.Config.Accounts.Any(a => _app.AuthService != null && _app.AuthService.IsSessionFor(a));
                    // 如果希沃长时间没请求 SSO（>5秒），认为已退出登录
                    if (loggedIn && _app.Gateway?.LastSsoRequestAtUtc != null)
                    {
                        var idle = DateTime.UtcNow - _app.Gateway.LastSsoRequestAtUtc.Value;
                        if (idle.TotalSeconds > 5) loggedIn = false;
                    }
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
                    var proc = System.Diagnostics.Process.GetProcessesByName("EasiNote").FirstOrDefault();
                    bool running = proc != null && proc.MainWindowHandle != IntPtr.Zero;
                    bool loggedIn = false;
                    if (running)
                        loggedIn = _app.Config.Accounts.Any(a => _app.AuthService != null && _app.AuthService.IsSessionFor(a));

                    if (running && !loggedIn)
                    {
                        // 希沃打开但未登录 → 自动显示遮罩
                        if (_app.CurrentOverlay == null || !_app.CurrentOverlay.IsVisible)
                            _app.ToggleOverlay();
                    }
                    else if (!running || loggedIn)
                    {
                        // 希沃关闭或已登录 → 关闭遮罩
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
