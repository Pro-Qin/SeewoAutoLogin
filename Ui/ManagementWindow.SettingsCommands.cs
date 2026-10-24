using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        private async Task HandleSetPasswordAsync(JsonElement root)
        {
            var pw = root.Str("password");
            if (string.IsNullOrEmpty(pw)) return;
            // PBKDF2 + 加盐，哈希值本身再用 DPAPI 包裹后落盘
            _app.SetPluginPassword(pw);
            await SendToJs(new { type = "settings", passwordSet = true });
        }

        private async Task HandleClearPasswordAsync()
        {
            _app.SetPluginPassword("");
            await SendToJs(new { type = "settings", passwordSet = false });
        }

        private async Task HandleUnlockAsync(JsonElement root)
        {
            var pw = root.Str("password");
            if (string.IsNullOrEmpty(pw)) { await SendToJs(new { type = "unlock-status", text = "请输入密码" }); return; }

            if (Services.PasswordService.IsLockedOut(out var seconds))
            {
                await SendToJs(new { type = "unlock-status", text = $"错误次数过多，请 {seconds} 秒后再试" });
                return;
            }

            var ok = _app.Config.UsePluginPassword && !string.IsNullOrEmpty(_app.Config.PluginPasswordHash)
                && _app.VerifyPluginPassword(pw);
            if (ok) { _unlocked = true; await SendToJs(new { type = "unlock-success" }); await SendSettings(); }
            else await SendToJs(new { type = "unlock-status", text = "密码错误" });
        }

        private void HandleUpdateSetting(JsonElement root)
        {
            var key = root.Str("key");
            var raw = root.Node("value");
            if (string.IsNullOrEmpty(key) || raw.ValueKind == JsonValueKind.Undefined) return;

            // 前端只会发真正的 bool / number。类型不符时整条丢弃，而不是把
            // 「没看懂的值」当成 false 写进配置 —— 关掉自动修复、关掉自启这类开关代价很大。
            var isBool = raw.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var flag = raw.ValueKind == JsonValueKind.True;
            var number = raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var parsed) ? parsed : 0;
            if (!isBool && key != "userListRotationGroupSize") return;

            switch (key)
            {
                case "usePluginPassword": _app.Config.UsePluginPassword = flag; break;
                case "userListRotationEnabled": _app.Config.UserListRotationEnabled = flag; break;
                case "userListRotationGroupSize": _app.Config.UserListRotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(number); break;
                case "minimizeToTray": _app.Config.MinimizeToTray = flag; break;
                case "startMinimized": _app.Config.StartMinimized = flag; break;
                case "restoreHostsOnExit": _app.Config.RestoreHostsOnExit = flag; break;
                case "autoRepairEnabled": _app.Config.AutoRepairEnabled = flag; break;
                case "autoShowOverlay": _app.Config.AutoShowOverlay = flag; break;
                case "particleBackground":
                    _app.Config.ParticleBackground = flag;
                    break;
                case "useSystemTitleBar":
                    _app.Config.UseSystemTitleBar = flag;
                    _app.WriteDiagnosticLog("[UI] 标题栏: " + (flag ? "系统" : "自绘") + "（下次打开窗口生效）");
                    break;
                case "performanceMode":
                    _app.Config.PerformanceMode = flag;
                    _app.WriteDiagnosticLog("[Perf] 性能模式：" + (_app.Config.PerformanceMode ? "已开启（关闭大量界面动效）" : "已关闭"));
                    break;
                case "autoCheckUpdate": _app.Config.AutoCheckUpdate = flag; break;
                case "autoInstallAfterDownload":
                    _app.Config.AutoInstallAfterDownload = flag;
                    _app.WriteDiagnosticLog("[Update] 下载完成后自动安装：" + (_app.Config.AutoInstallAfterDownload ? "已开启" : "已关闭"));
                    break;
                case "autoStart":
                    {
                        // 与欢迎界面共用同一份状态：优先创建最高权限计划任务（开机免 UAC），失败回退注册表启动项
                        var enabled = flag;
                        if (enabled)
                        {
                            var ok = AutoStartService.Enable(out var error, out var mode);
                            _app.WriteDiagnosticLog($"[AutoStart] 启用自启: ok={ok}; mode={mode}; {error}");
                            // 只有真的写成功才把配置标成已启用。失败时（用户拒绝提权、计划任务没建成）
                            // 系统里没有启动项、配置却记着「已启用」，两边对不上。
                            _app.Config.AutoStartEnabled = ok;
                            if (!ok || !string.IsNullOrEmpty(error)) _app.TrayIcon?.Notify(error);
                            if (!ok)
                            {
                                // 计划任务需要管理员权限：自动询问是否提权重启（用户确认后才重启）
                                if (!AutoStartService.IsAdministrator())
                                {
                                    var confirmed = Dialogs.Confirm(this, "需要管理员权限",
                                        "创建「最高权限计划任务」需要管理员权限，当前以普通权限运行。\n\n" +
                                        "是否现在以管理员身份重新启动本程序？（重启后开机不再需要授权）\n\n" +
                                        "  · 是   → 提权重启并重新尝试\n" +
                                        "  · 否   → 暂时保持当前设置");
                                    if (confirmed)
                                    {
                                        _app.Config.AutoStartEnabled = true;
                                        _app.SaveConfig();
                                        _app.RestartApp();
                                        return;
                                    }
                                }
                                else
                                {
                                    _app.NotifyError("开机自启未开启", "写入开机自启失败：" + error);
                                }
                            }
                        }
                        else
                        {
                            _app.Config.AutoStartEnabled = false;
                            if (!AutoStartService.Disable(out var offError))
                                _app.WriteDiagnosticLog($"[AutoStart] 停用自启失败: {offError}");
                        }
                        break;
                    }
            }
            _app.SaveConfig();
        }

        /// <summary>教程结束：记录状态，避免每次启动都自动播放</summary>
        private void HandleTourDone(JsonElement root)
        {
            var completed = root.Flag("completed");
            _app.Config.PendingTour = false;
            if (completed) _app.Config.TourCompleted = true;
            _app.SaveConfig();
            _app.WriteDiagnosticLog($"[Tour] 教程结束; completed={completed}");
            _ = SendToJs(new { type = "toast", text = "教程结束，随时可在「设置 → 帮助与维护」里重看", level = "ok" });
        }

        /// <summary>恢复出厂设置：前端已确认一次，这里再确认一次并说明后果</summary>
        private void HandleFactoryReset()
        {
            var confirmed = Dialogs.ConfirmDanger(this, "恢复出厂设置",
                "恢复出厂设置会：\n" +
                "  · 删除全部账号（含扫码凭据）\n" +
                "  · 清空所有设置与配置备份\n" +
                "  · 回到首次安装的引导流程\n\n" +
                "此操作不可撤销，确定继续吗？");

            if (!confirmed)
            {
                _ = SendToJs(new { type = "factory-reset-cancelled" });
                return;
            }

            _app.FactoryReset();
            _ = SendToJs(new { type = "factory-reset-done" });
            _app.WriteDiagnosticLog("[Reset] 即将自动重启，回到首次使用流程");

            // 稍等一下让界面把提示画出来，然后直接重启（不必让用户自己再点一次）
            var restartTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            restartTimer.Tick += (_, _) =>
            {
                restartTimer.Stop();
                _app.RestartApp();
            };
            restartTimer.Start();
        }

        /// <summary>下发切换口令的启用状态</summary>
        private async Task SendSwitchPinStatusAsync()
        {
            try { await SendToJs(new { type = "switch-pin", enabled = _app.SwitchPin.IsEnabled }); }
            catch { }
        }

        private async Task SetSwitchPinAsync(JsonElement root)
        {
            // 已经设过口令时，改口令必须先验证旧口令 —— 否则任何人打开设置就能把口令换掉，功能白做
            if (_app.SwitchPin.IsEnabled && !RequireSwitchPin()) return;

            var pin = root.Str("pin", null);
            var (ok, error) = _app.SwitchPin.Set(pin ?? "");
            if (!ok) { await SendToast("设置失败：" + error, "error"); return; }
            _app.WriteDiagnosticLog("[SwitchPin] 已设置切换口令");
            await SendToast("切换口令已设置：以后切换账号需要先输入", "ok");
            await SendSwitchPinStatusAsync();
        }

        private async Task ClearSwitchPinAsync()
        {
            // 清除口令同样要先验证：口令的意义就是拦住「随手切换账号」的人，
            // 如果连清除都不设防，点一下「清除口令」就绕过去了。
            if (_app.SwitchPin.IsEnabled && !RequireSwitchPin())
            {
                // 验证失败时回填开关状态，避免界面停在「已取消勾选」的假象
                await SendSwitchPinStatusAsync();
                return;
            }

            _app.SwitchPin.Clear();
            _app.WriteDiagnosticLog("[SwitchPin] 已清除切换口令");
            await SendToast("已清除切换口令，切换账号不再需要验证", "ok");
            await SendSwitchPinStatusAsync();
        }

        /// <summary>
        /// 需要口令的账号切换操作前调用。未设置口令、或验证通过时返回 true。
        /// 面向一台班班设备多位老师共用的场景，避免随手点开别人的账号。
        /// </summary>
        private bool RequireSwitchPin()
        {
            if (!_app.SwitchPin.IsEnabled) return true;

            var dialog = new TextInputDialog("输入切换口令", "切换账号需要口令：", "") { Owner = this };
            if (dialog.ShowDialog() != true) return false;

            if (_app.SwitchPin.Verify(dialog.InputText ?? "")) return true;

            _app.WriteDiagnosticLog("[SwitchPin] 切换口令校验失败");
            _ = SendToast("口令不正确", "error");
            return false;
        }

        /// <summary>
        /// 下发凭据有效期观测结论。样本来自后台每次续期的实测记录，
        /// 样本不足时说明"暂无结论"，不编造。
        /// </summary>
        private async Task SendCredentialLifetimeAsync()
        {
            string text;
            try { text = _app.CredentialLifetimeDescription ?? ""; }
            catch { text = ""; }
            await SendToJs(new { type = "credential-lifetime", text });
        }

        /// <summary>弹一个输入框要口令（明文可读，属于一次性输入，够用）</summary>
        private string PromptSecret(string title, string hint)
        {
            var dialog = new TextInputDialog(title, hint, "") { Owner = this };
            return dialog.ShowDialog() == true ? (dialog.InputText ?? "").Trim() : null;
        }

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleSetPassword(JsonElement root) => RunCommand(() => HandleSetPasswordAsync(root), "HandleSetPassword");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleClearPassword() => RunCommand(() => HandleClearPasswordAsync(), "HandleClearPassword");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleUnlock(JsonElement root) => RunCommand(() => HandleUnlockAsync(root), "HandleUnlock");
    }
}