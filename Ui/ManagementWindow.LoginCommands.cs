namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        private async Task HandleLogin(JsonElement root)
        {
            var username = root.Str("username");
            var password = root.Str("password");
            var displayName = root.Str("displayName");
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            { await SendToJs(new { type = "login-status", text = "请输入账号和密码" }); return; }

            _passwordLoginCancellation?.Cancel(); _passwordLoginCancellation?.Dispose();
            _passwordLoginCancellation = new CancellationTokenSource();
            var ct = _passwordLoginCancellation;
            try
            {
                await SendToJs(new { type = "login-status", text = "正在验证..." });
                var svc = new SeewoAuthService();
                // 记一条开始日志：出问题时才能区分「用户没点过」和「点了但失败了」
                _app?.WriteDiagnosticLog($"[Login] 开始密码登录: user={username}");
                var result = await svc.LoginAsync(username, password, ct.Token);
                if (result.Success)
                {
                    var acct = new SeewoAccount
                    {
                        DisplayName = string.IsNullOrEmpty(displayName) ? (result.UserInfo?.NickName ?? username) : displayName,
                        Username = username, Password = password, UserInfo = result.UserInfo
                    };
                    _app.AddAccount(acct);
                    Services.LoginHistoryService.Record(acct.Id, acct.DisplayName, acct.Username, "password", true, "界面密码登录");
                    await SendToJs(new { type = "login-status", text = "登录成功，已保存" });
                    await Task.Delay(600);
                    await RefreshAccountList();
                }
                else
                {
                    // 失败原因只回给界面的话，日志里什么都查不到（密码登录不像扫码那样有详细 HTTP 日志）
                    _app?.WriteDiagnosticLog($"[Login] 密码登录失败: kind={result.FailureKind}; message={result.ErrorMessage}");
                    await SendToJs(new { type = "login-status", text = $"登录失败: {result.ErrorMessage}" });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Login] 密码登录异常: {ex}");
                await SendToJs(new { type = "login-status", text = $"登录失败: {ex.Message}" });
            }
            finally { if (ReferenceEquals(_passwordLoginCancellation, ct)) { _passwordLoginCancellation.Dispose(); _passwordLoginCancellation = null; } }
        }
    }
}
