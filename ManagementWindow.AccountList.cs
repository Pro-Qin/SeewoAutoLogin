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
        #region Account List

        /// <summary>供后台保活调用：账号状态变化后刷新界面（主界面未打开时不会走到这里）</summary>
        internal Task RefreshAccountListAsync() => RefreshAccountList();

        private async Task RefreshAccountList()
        {
            await SendToJs(new { type = "account-list", accounts = GetAccountList() });
            await SendStatus();
        }

        private object GetAccountList()
        {
            return _app.Config.Accounts.Select(a => new
            {
                id = a.Id,
                displayName = a.DisplayName ?? a.Username ?? "",
                username = a.Username ?? "",
                loginType = string.IsNullOrEmpty(a.Password) ? "扫码" : "密码",
                isActive = a.Id == _app.Config.ActiveAccountId,
                initial = (a.DisplayName ?? a.Username ?? "S").Substring(0, 1).ToUpperInvariant(),
                tags = a.Tags != null && a.Tags.Count > 0 ? string.Join(", ", a.Tags) : "",
                requestCount = a.RequestCount,
                lastRequestAtUtc = a.LastRequestAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                healthState = a.HealthState ?? "",
                healthMessage = a.HealthMessage ?? "",
                lastHealthCheckAtUtc = a.LastHealthCheckAtUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "",
                // 后台自动续期的时间：账号卡片上会显示「已自动续期 · 3 分钟前」
                lastTokenExchangeAtUtc = a.LastTokenExchangeAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                isPlaceholder = a.IsPlaceholder
            }).ToList();
        }

        /// <summary>网关向希沃返回账号列表后，由 App 调用以刷新计数展示</summary>
        internal void NotifyAccountsServed()
        {
            if (!_webViewReady) return;
            _ = RefreshAccountList();
        }

        #endregion
    }
}
