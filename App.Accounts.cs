using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SeewoAutoLogin.Services;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Account Management

        public IReadOnlyList<SeewoAccount> GetVisibleAccounts() => _config.Accounts;

        public void AddQrAccount(SeewoAccount account, QrLoginOutcome outcome)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.Token))
                throw new ArgumentException("扫码登录结果无效。", nameof(outcome));

            var existing = FindMatchingAccount(account.UserInfo);
            var credentialId = existing?.QrCredentialId;
            if (string.IsNullOrWhiteSpace(credentialId))
                credentialId = _qrSessionStore.CreateCredentialId();

            _qrSessionStore.Save(credentialId, outcome.Token, DateTimeOffset.UtcNow);
            account.QrCredentialId = credentialId;

            if (existing != null)
            {
                existing.DisplayName = account.DisplayName;
                existing.Username = account.Username;
                existing.Password = "";
                existing.UserInfo = account.UserInfo;
                existing.QrCredentialId = credentialId;
                if (_config.ActiveAccountId == "") _config.ActiveAccountId = existing.Id;
                SaveConfig();
                WriteDiagnosticLog($"[Account] 已更新扫码账号会话; account-id={existing.Id}");
                return;
            }

            AddAccount(account);
        }

        public void AddAccount(SeewoAccount account)
        {
            _config.Accounts.Add(account);
            if (_config.Accounts.Count == 1)
                _config.ActiveAccountId = account.Id;
            SaveConfig();
            WriteDiagnosticLog($"[Account] 已保存账号; account-id={account.Id}");
        }

        public void RemoveAccount(string accountId)
        {
            var account = _config.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (!string.IsNullOrWhiteSpace(account?.QrCredentialId))
            {
                try { _qrSessionStore.Delete(account.QrCredentialId); }
                catch { }
            }
            _config.Accounts.RemoveAll(a => a.Id == accountId);
            if (_config.ActiveAccountId == accountId)
                _config.ActiveAccountId = _config.Accounts.FirstOrDefault()?.Id ?? "";
            SaveConfig();
        }

        public void SwitchActiveAccount(string accountId)
        {
            _config.ActiveAccountId = accountId;
            _authService.Logout();
            SaveConfig();
            WriteDiagnosticLog($"[Account] 切换到账号; account-id={accountId}");
        }

        /// <summary>切换遮罩层显示/隐藏（仅由托盘触发；遮罩自带 20s 自动关闭）</summary>
        public void ToggleOverlay()
        {
            try
            {
                if (_overlay != null && _overlay.IsVisible)
                {
                    var closing = _overlay;
                    _overlay = null;
                    closing.Close();
                    return;
                }

                // 账号不足时遮罩会在构造阶段自行 Close，这里提前判断，避免“Show 已关闭窗口”抛异常
                var unlistedCount = Math.Max(0, _config.Accounts.Count - PluginConfig.MaxVisibleAccounts);
                if (unlistedCount == 0)
                {
                    NotifyInfo(Strings.AppTitle, $"当前 {_config.Accounts.Count} 个账号都已在希沃生效区（上限 {PluginConfig.MaxVisibleAccounts} 个），无需切换遮罩。");
                    return;
                }

                var overlay = new SeewoOverlay();
                overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };
                _overlay = overlay;
                overlay.Show();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Overlay] 显示切换遮罩失败: {ex.GetType().Name} - {ex.Message}");
                NotifyError("显示遮罩失败", "无法显示账号切换遮罩：\n" + ex.Message);
            }
        }

        /// <summary>托盘右键菜单：将未展示的账号提到最前，或执行重启/退出</summary>
        public void SwitchToAccount(string accountId)
        {
            if (accountId == "__RESTART__") { RestartApp(); return; }
            if (accountId == "__EXIT__") { BeginExit(); return; }
            if (accountId == "__OVERLAY__") { ToggleOverlay(); return; }
            if (RequiresPasswordUnlock()) return;
            var account = _config.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return;
            _config.Accounts.Remove(account);
            _config.Accounts.Insert(0, account);
            WriteDiagnosticLog($"[Tray] 已将账号 {account.DisplayName ?? account.Username} 提到最前");
            SaveConfig();
            // 刷新托盘菜单
            _trayIcon.UpdateVisibleAccounts(new List<string>());
        }

        /// <summary>托盘切换账号属于配置变更：启用密码保护时要求先验证密码（返回 true 表示应拒绝执行）</summary>
        private bool RequiresPasswordUnlock()
        {
            if (!_config.UsePluginPassword || string.IsNullOrEmpty(_config.PluginPasswordHash))
                return false;

            if (Services.PasswordService.IsLockedOut(out var secondsRemaining))
            {
                NotifyError(Strings.AppTitle, $"密码错误次数过多，请在 {secondsRemaining} 秒后重试。");
                return true;
            }

            try
            {
                // 注意：isPassword 必须通过构造函数传入，用对象初始化器赋值不会切换输入框可见性（会导致明文回显 + 校验恒失败）
                var owner = _mainWindow != null && _mainWindow.IsLoaded ? _mainWindow : null;
                var dlg = new TextInputDialog(Strings.AppTitle, Strings.EnterPassword, "", isPassword: true)
                {
                    Owner = owner
                };
                if (dlg.ShowDialog() != true) return true; // 取消 = 不执行
                return !VerifyPluginPassword(dlg.InputText);
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Tray] 密码验证对话框异常: {ex.GetType().Name}");
                return true; // fail-closed：校验过程出错时按“需要解锁”处理，不允许静默绕过
            }
        }

        /// <summary>校验应用设置口令（PBKDF2 + DPAPI；兼容旧的单轮 SHA-256 并在成功时自动升级）</summary>
        internal bool VerifyPluginPassword(string password)
        {
            var ok = Services.PasswordService.Verify(
                password, _config.PluginPasswordHash, _config.PluginPasswordSalt, out var upgraded);
            if (ok && upgraded)
            {
                try
                {
                    Services.PasswordService.Create(password, out var hash, out var salt);
                    _config.PluginPasswordHash = hash;
                    _config.PluginPasswordSalt = salt;
                    SaveConfig();
                    WriteDiagnosticLog("[Security] 设置口令哈希已升级为 PBKDF2 + 加盐 + DPAPI 保护");
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Security] 口令哈希升级失败: {ex.Message}");
                }
            }
            return ok;
        }

        /// <summary>设置/清除应用设置口令（hash 由 PasswordService 生成，落盘前已用 DPAPI 包裹）</summary>
        internal void SetPluginPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                _config.UsePluginPassword = false;
                _config.PluginPasswordHash = "";
                _config.PluginPasswordSalt = "";
            }
            else
            {
                Services.PasswordService.Create(password, out var hash, out var salt);
                _config.UsePluginPassword = true;
                _config.PluginPasswordHash = hash;
                _config.PluginPasswordSalt = salt;
            }
            SaveConfig();
        }

        private SeewoAccount FindMatchingAccount(SeewoUserInfo userInfo)
        {
            if (userInfo == null) return null;
            return _config.Accounts.FirstOrDefault(account =>
                (!string.IsNullOrWhiteSpace(userInfo.AccountId) &&
                 string.Equals(account.UserInfo?.AccountId, userInfo.AccountId, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(userInfo.UserName) &&
                 string.Equals(account.UserInfo?.UserName, userInfo.UserName, StringComparison.Ordinal)));
        }

        public void OnQrTokenValidated(SeewoAccount account, string token)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.QrCredentialId) || string.IsNullOrWhiteSpace(token))
                return;
            try
            {
                _qrSessionStore.Save(account.QrCredentialId, token, DateTimeOffset.UtcNow);
                WriteDiagnosticLog($"[Session] Token 换发返回新 Token; account-id={account.Id}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Session] 更新 DPAPI 凭据失败; account-id={account.Id}; error={ex.GetType().Name}");
            }
        }

        public bool TryRestoreQrSession(SeewoAccount account)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.QrCredentialId))
            {
                WriteDiagnosticLog($"[Session] 扫码账号没有可恢复的凭据; account-id={account?.Id ?? "<none>"}");
                return false;
            }

            if (!_qrSessionStore.TryLoad(account.QrCredentialId, out var session))
            {
                WriteDiagnosticLog($"[Session] 扫码凭据读取失败; account-id={account.Id}");
                return false;
            }

            _authService.RestoreQrSession(session.Token, account.UserInfo);
            var restored = _authService.IsSessionFor(account);
            WriteDiagnosticLog($"[Session] 扫码会话恢复; account-id={account.Id}; restored={restored}");
            return restored;
        }

        #endregion
    }
}
