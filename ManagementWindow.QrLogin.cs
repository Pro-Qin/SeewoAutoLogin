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
        #region QR Login

        private async void QrLoginCoordinator_StateChanged(object sender, QrLoginStateChangedEventArgs e)
        {
            await Dispatcher.BeginInvoke(new Action(async () => await RenderQrState(e)));
        }

        private async Task RenderQrState(QrLoginStateChangedEventArgs e)
        {
            var active = e.State is QrLoginState.CreatingQrCode or QrLoginState.WaitingForScan or QrLoginState.WaitingForConfirmation or QrLoginState.Completing;
            var s = e.State switch
            {
                QrLoginState.CreatingQrCode => "creating", QrLoginState.WaitingForScan => "waiting-scan",
                QrLoginState.WaitingForConfirmation => "waiting-confirm", QrLoginState.Completing => "completing",
                QrLoginState.Succeeded => "succeeded", QrLoginState.Expired => "expired",
                QrLoginState.Cancelled => "cancelled", QrLoginState.Denied => "denied",
                QrLoginState.NetworkError => "network-error", QrLoginState.ProtocolError => "protocol-error", _ => "idle"
            };
            string imgData = null;
            if (e.Session?.ImageBytes.Length > 0) { imgData = "data:image/png;base64," + Convert.ToBase64String(e.Session.ImageBytes); _qrExpiresAt = e.Session.ExpiresAt; _qrCountdownTimer.Start(); }
            var text = e.State switch
            {
                QrLoginState.CreatingQrCode => "正在创建二维码...", QrLoginState.WaitingForScan => "请使用希沃App扫码",
                QrLoginState.WaitingForConfirmation => "请在手机上确认", QrLoginState.Completing => "正在登录...",
                QrLoginState.Succeeded => "登录成功！", QrLoginState.Expired => "二维码已过期",
                QrLoginState.Cancelled => "已取消", QrLoginState.Denied => "已拒绝",
                QrLoginState.NetworkError => "网络错误", QrLoginState.ProtocolError => string.IsNullOrWhiteSpace(e.Message) ? "协议错误" : e.Message, _ => ""
            };
            if (e.State == QrLoginState.Succeeded) _qrCountdownTimer.Stop();
            await SendToJs(new { type = "qr-state", state = s, imageData = imgData, text });
            if (!active) { _qrCountdownTimer.Stop(); await SendToJs(new { type = "qr-countdown", text = "" }); }
        }

        private void QrCountdownTimer_Tick(object sender, EventArgs e)
        {
            var sec = Math.Max(0, (int)Math.Ceiling((_qrExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
            _ = SendToJs(new { type = "qr-countdown", text = $"{sec} 秒后过期" });
            if (sec == 0) _qrCountdownTimer.Stop();
        }

        private async Task StartQrLoginAsync()
        {
            _qrLoginCancellation?.Cancel(); _qrLoginCancellation?.Dispose();
            _qrLoginCancellation = new CancellationTokenSource();
            var ct = _qrLoginCancellation;
            try
            {
                var outcome = await _qrLoginCoordinator.StartAsync(ct.Token);
                if (outcome == null || ct.IsCancellationRequested) return;
                _authService.AcceptQrLogin(outcome);
                var uname = !string.IsNullOrWhiteSpace(outcome.UserInfo?.Phone) ? outcome.UserInfo.Phone : outcome.UserInfo?.UserName ?? "";
                var acct = new SeewoAccount
                {
                    DisplayName = outcome.UserInfo?.NickName ?? outcome.UserInfo?.RealName ?? "未命名",
                    Username = uname, Password = "", UserInfo = outcome.UserInfo
                };
                _app.AddQrAccount(acct, outcome);
                await RefreshAccountList();
                await SendToJs(new { type = "qr-state", state = "succeeded", text = "登录成功！" });
            }
            finally { if (ReferenceEquals(_qrLoginCancellation, ct)) { _qrLoginCancellation.Dispose(); _qrLoginCancellation = null; } }
        }

        #endregion
    }
}
