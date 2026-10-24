using Microsoft.Web.WebView2.Core;
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
            // 先取快照再渲染：后台保活在线程池线程上触发刷新时，界面线程可能正在增删账号
            var accounts = _app.Config.Accounts.ToList();
            return accounts.Select(a => new
            {
                id = a.Id,
                displayName = a.DisplayName ?? a.Username ?? "",
                username = a.Username ?? "",
                loginType = string.IsNullOrEmpty(a.Password) ? "扫码" : "密码",

                initial = (a.DisplayName ?? a.Username ?? "S").Substring(0, 1).ToUpperInvariant(),
                // 希沃头像：登录成功后 UserInfo 里带 photoUrl，卡片直接用它（拿不到就退回首字母）
                photoUrl = a.UserInfo?.PhotoUrl ?? "",
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
