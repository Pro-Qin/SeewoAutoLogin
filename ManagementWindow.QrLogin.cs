using Microsoft.Web.WebView2.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        #region QR Login

        /// <summary>
        /// 扫码状态变化：事件来自后台线程，必须回到 UI 线程再渲染。
        /// 这是 async void（事件签名要求），所以渲染异常必须在这里就地吃掉 ——
        /// 逃出去就是 DispatcherUnhandledException，托盘程序会直接消失。
        /// </summary>
        private void QrLoginCoordinator_StateChanged(object sender, QrLoginStateChangedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await RenderQrState(e);
                }
                catch (Exception ex)
                {
                    _app?.WriteDiagnosticLog($"[QR] 状态渲染失败: {ex.GetType().Name} - {ex.Message}");
                }
            }));
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
                Services.LoginHistoryService.Record(acct.Id, acct.DisplayName, acct.Username, "qr", true, "界面扫码登录");
                await RefreshAccountList();
                await SendToJs(new { type = "qr-state", state = "succeeded", text = "登录成功！" });
            }
            catch (OperationCanceledException)
            {
                // 连点两次「开始扫码」会取消前一次：不把这个异常收掉，界面会永远停在「正在获取二维码…」
                await SendToJs(new { type = "qr-state", state = "cancelled", text = "已取消扫码" });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Qr] 扫码登录异常: {ex.GetType().Name} - {ex.Message}");
                await SendToJs(new { type = "qr-state", state = "failed", text = "扫码登录出错，可重试：" + ex.Message });
            }
            finally { if (ReferenceEquals(_qrLoginCancellation, ct)) { _qrLoginCancellation.Dispose(); _qrLoginCancellation = null; } }
        }

        #endregion
    }
}
