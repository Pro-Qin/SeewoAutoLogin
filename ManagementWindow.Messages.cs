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
        #region JS → C# (Message Handler)

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                // 来源校验：消息是唯一的特权入口（删账号、导配置、退出、恢复出厂都走它），
                // 不能只看「有没有 postMessage」，还得看是谁发的。
                if (!IsTrustedPageUri(e.Source))
                {
                    _app?.WriteDiagnosticLog($"[WebView] 丢弃来源不可信的消息: {e.Source}");
                    return;
                }

                // 解锁前只放行 unlock，其余消息（含托盘可触达的配置变更）全部忽略
                if (type != "unlock" && IsLocked) return;

                switch (type)
                {
                    case "login": await HandleLogin(root); break;
                    case "start-qr": _ = StartQrLoginAsync(); break;
                    case "cancel-qr": _qrLoginCancellation?.Cancel(); _qrLoginCoordinator?.Cancel(); break;
                    case "delete-account": HandleDeleteAccount(root); break;

                    case "move-up": HandleMove(root, -1); break;
                    case "move-down": HandleMove(root, 1); break;
                    case "edit-tags": HandleEditTags(root); break;
                    case "edit-tags-dialog": HandleEditTagsDialog(root); break;
                    case "edit-display-name": HandleEditDisplayName(root); break;
                    case "rename-account-dialog": await HandleRenameAccountDialogAsync(root); break;
                    case "add-fake-account-dialog": await HandleAddFakeAccountDialogAsync(); break;
                    case "export-config": HandleExportConfig(); break;
                    case "export-accounts": await HandleExportAccountsAsync(); break;
                    case "get-switch-pin": await SendSwitchPinStatusAsync(); break;
                    case "set-switch-pin": await SetSwitchPinAsync(root); break;
                    case "clear-switch-pin": await ClearSwitchPinAsync(); break;
                    case "get-credential-lifetime": await SendCredentialLifetimeAsync(); break;
                    case "import-accounts": await HandleImportAccountsAsync(); break;
                    case "import-config": HandleImportConfig(); break;
                    case "open-log-viewer": new LogViewerWindow { Owner = this }.ShowDialog(); break;
                    case "open-diagnostic": new DiagnosticWindow { Owner = this }.ShowDialog(); break;
                    case "restart": _app.RestartApp(); break;
                    case "exit": _app.BeginExit(); break;
                    case "set-password": HandleSetPassword(root); break;
                    case "clear-password": HandleClearPassword(); break;
                    case "update-setting": HandleUpdateSetting(root); break;
                    case "unlock": HandleUnlock(root); break;
                    case "add-fake-account": await HandleAddFakeAccount(root); break;
                    case "refresh-debug": await SendSeewoStatus(); break;
                    case "move-to-active": HandleMoveToActive(root); break;
                    case "move-to-inactive": HandleMoveToInactive(root); break;
                    case "toggle-overlay": _app.ToggleOverlay(); break;
                    case "check-update": await HandleCheckUpdateAsync(manual: true); break;
                    case "open-update-page": HandleOpenUpdatePage(root); break;
                    case "repair-sso": await HandleRepairSsoAsync(); break;
                    case "health-check": await HandleHealthCheckAsync(); break;
                    case "batch-import": await HandleBatchImportAsync(root); break;
                    case "import-csv": await HandleCsvImportAsync(); break;
                    case "list-backups": await SendBackups(); break;
                    case "restore-backup": await HandleRestoreBackupAsync(root); break;
                    case "tour-started": _app.WriteDiagnosticLog("[Tour] 用户开始观看使用教程"); break;
                    case "tour-debug": _app.WriteDiagnosticLog("[Tour] " + (root.TryGetProperty("text", out var dbg) ? dbg.GetString() : "")); break;
                    case "tour-done": HandleTourDone(root); break;
                    case "factory-reset": HandleFactoryReset(); break;
                    case "export-diagnostics": await HandleExportDiagnosticsAsync(); break;
                    case "export-logs-to-desktop": await HandleExportLogsToDesktopAsync(); break;
                    case "get-terms": await SendTerms(); break;
                    case "open-external": HandleOpenExternal(root); break;
                    case "reorder-accounts": HandleReorderAccounts(root); break;
                    case "renew-account": await HandleRenewAccountsAsync(root); break;
                    case "get-login-history": await HandleGetLoginHistory(root); break;
                    case "clear-login-history":
                        LoginHistoryService.Clear();
                        await HandleGetLoginHistory(root);
                        break;
                    case "download-update": HandleDownloadUpdate(); break;
                    case "cancel-download": HandleCancelDownload(); break;
                    case "debug-upgrade-notice": await HandleDebugUpgradeNoticeAsync(); break;
                    case "debug-desktop-notice": HandleDebugDesktopNotice(); break;
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine 在发布版没有任何输出：用户报「点了没反应」时日志里查不到线索
                _app?.WriteDiagnosticLog($"[WebView] 消息处理异常: {ex.GetType().Name} - {ex.Message}");
            }
        }

        #endregion
    }
}
